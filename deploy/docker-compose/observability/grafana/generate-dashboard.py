"""Generates the provisioned Grafana dashboards. Edit this file, not the JSON, then run from this directory:

    python3 generate-dashboard.py dashboards

ObservabilityConfigTests regenerates them and fails when the committed JSON differs, and checks that every metric a
panel queries is in the application metric catalog or comes from a known exporter.

Metric names are the Prometheus translations of the OpenTelemetry names (ADR-017 §5): dots become underscores and
the unit/`_total` suffixes are appended, e.g. `http.server.request.duration` -> `http_server_request_duration_seconds`.
"""
import json
import os
import sys

PROM = {"type": "prometheus", "uid": "prometheus"}
LOKI = {"type": "loki", "uid": "loki"}
JAEGER = {"type": "jaeger", "uid": "jaeger"}
RATE = "$__rate_interval"
API = 'job="opportunity/opportunity-api"'


class Dashboard:
    def __init__(self, uid, title, description, links=()):
        self.uid = uid
        self.title = title
        self.description = description
        self.links = list(links)
        self.panels = []
        self.next_id = 1
        self.y = 0

    def add(self, panel, x, y, w, h):
        panel["id"] = self.next_id
        self.next_id += 1
        panel["gridPos"] = {"x": x, "y": y, "w": w, "h": h}
        self.panels.append(panel)

    def row(self, title):
        self.add({"type": "row", "title": title, "collapsed": False, "panels": []}, 0, self.y, 24, 1)
        self.y += 1

    def line(self, panels, h=8):
        """Lays out panels left to right on one line of equal-width cells."""
        w = 24 // len(panels)
        for i, panel in enumerate(panels):
            self.add(panel, i * w, self.y, w, h)
        self.y += h

    def json(self):
        return {
            "uid": self.uid,
            "title": self.title,
            "description": self.description,
            "tags": ["opportunity"],
            "timezone": "browser",
            "schemaVersion": 41,
            "version": 1,
            "editable": False,
            "refresh": "30s",
            "time": {"from": "now-1h", "to": "now"},
            "templating": {"list": []},
            "annotations": {"list": []},
            "links": [{"type": "dashboards", "title": "opportuniTY dashboards", "tags": ["opportunity"], "asDropdown": True,
                       "includeVars": False, "keepTime": True}] + self.links,
            "panels": self.panels,
        }


def ts(title, targets, unit, description="", stack=False, thresholds=None):
    defaults = {
        "unit": unit,
        "custom": {"lineWidth": 1, "fillOpacity": 10, "showPoints": "never",
                   "stacking": {"mode": "normal" if stack else "none"}},
    }
    if thresholds is not None:
        defaults["custom"]["thresholdsStyle"] = {"mode": "dashed"}
        defaults["thresholds"] = {"mode": "absolute",
                                  "steps": [{"color": "green", "value": None}, {"color": "red", "value": thresholds}]}
    return {
        "type": "timeseries",
        "title": title,
        "description": description,
        "datasource": PROM,
        "fieldConfig": {"defaults": defaults, "overrides": []},
        "options": {"legend": {"displayMode": "list", "placement": "bottom"}, "tooltip": {"mode": "multi"}},
        "targets": [
            {"refId": chr(65 + i), "datasource": PROM, "expr": expr, "legendFormat": legend}
            for i, (expr, legend) in enumerate(targets)
        ],
    }


def stat(title, expr, unit, legend="", mappings=None, thresholds=None, description=""):
    return {
        "type": "stat",
        "title": title,
        "description": description,
        "datasource": PROM,
        "fieldConfig": {
            "defaults": {
                "unit": unit,
                "mappings": mappings or [],
                "thresholds": thresholds or {"mode": "absolute", "steps": [{"color": "green", "value": None}]},
            },
            "overrides": [],
        },
        "options": {"reduceOptions": {"calcs": ["lastNotNull"]}, "colorMode": "background", "graphMode": "none",
                    "textMode": "value_and_name"},
        "targets": [{"refId": "A", "datasource": PROM, "expr": expr, "legendFormat": legend, "instant": True}],
    }


def limit(*steps):
    """Thresholds: green below the first step, then orange/red."""
    colors = ["orange", "red"] if len(steps) == 2 else ["red"]
    return {"mode": "absolute",
            "steps": [{"color": "green", "value": None}] + [{"color": c, "value": v} for c, v in zip(colors, steps)]}


def p95(metric, by, selector=""):
    return f'histogram_quantile(0.95, sum by (le, {by}) (rate({metric}_bucket{selector}[{RATE}])))'


# --- Overview (E19-T04) -----------------------------------------------------------------------------------------------

def overview():
    d = Dashboard("opportunity-overview", "opportuniTY overview",
                  "Services, API, workers, data stores, runtime, traces and logs (E19-T04). Search consistency and the "
                  "job pipeline are on the 'opportuniTY search consistency and pipeline' dashboard (E19-T05).")
    d.row("Services")
    d.line([
        stat("Targets up", 'up', "none", "{{job}}",
             mappings=[{"type": "value", "options": {"0": {"text": "down", "color": "red"}, "1": {"text": "up", "color": "green"}}}],
             description="Scraped infrastructure exporters and the collector."),
        stat("Worker heartbeat age", 'max by (worker_type) (opportunity_worker_heartbeat_age_seconds)', "s", "{{worker_type}}",
             thresholds=limit(60, 120),
             description="opportunity.worker.heartbeat.age per worker type; > 2 heartbeat intervals means a stuck module (alert WorkerHeartbeatStale)."),
    ], h=4)

    d.row("API (opportunity-api)")
    d.line([
        ts("Requests per second by route",
           [(f'sum by (http_route, http_request_method) (rate(http_server_request_duration_seconds_count{{{API}}}[{RATE}]))', "{{http_request_method}} {{http_route}}")],
           "reqps"),
        ts("Latency p95 by route",
           [(f'histogram_quantile(0.95, sum by (le, http_route) (rate(http_server_request_duration_seconds_bucket{{{API}}}[{RATE}])))', "{{http_route}}")],
           "s", "Baseline §17: simple search p95 < 1 s, complex < 3 s."),
        ts("Error ratio (5xx)",
           [(f'(sum(rate(http_server_request_duration_seconds_count{{{API}, http_response_status_code=~"5.."}}[{RATE}])) or vector(0)) / clamp_min(sum(rate(http_server_request_duration_seconds_count{{{API}}}[{RATE}])), 1e-9)', "5xx")],
           "percentunit", "§17 API availability target 99.9% (alert ApiErrorBudgetBurn)."),
    ])

    d.row("Workers and messaging")
    d.line([
        ts("Messages processed per second",
           [(f'sum by (worker_type, messaging_destination_name) (rate(messaging_client_consumed_messages_total[{RATE}]))', "{{worker_type}} {{messaging_destination_name}}")],
           "ops", "messaging.client.consumed.messages."),
        ts("Message processing p95",
           [(p95("messaging_process_duration_seconds", "worker_type"), "{{worker_type}}")], "s"),
        ts("RabbitMQ messages (ready / unacked)",
           [("sum(rabbitmq_queue_messages_ready)", "ready"), ("sum(rabbitmq_queue_messages_unacked)", "unacked")],
           "short", "From the rabbitmq_prometheus plugin (aggregated). Per-queue depth, DLQ and consumers are on the pipeline dashboard."),
    ])

    d.row("PostgreSQL and OpenSearch")
    d.line([
        ts("PostgreSQL client operation p95 (Npgsql)",
           [(p95("db_client_operation_duration_seconds", "job"), "{{job}}")], "s"),
        ts("PostgreSQL backends",
           [('sum by (datname) (pg_stat_database_numbackends{datname!~"template.*"})', "{{datname}}")],
           "short", "postgres_exporter."),
        ts("OpenSearch JVM heap used",
           [('sum by (name) (elasticsearch_jvm_memory_used_bytes{area="heap"})', "{{name}}")],
           "bytes", "elasticsearch_exporter against the product cluster (metrics only; logs never go to OpenSearch, Q-42)."),
    ])

    d.row(".NET runtime")
    d.line([
        ts("Working set", [("sum by (job) (dotnet_process_memory_working_set_bytes)", "{{job}}")], "bytes"),
        ts("GC collections per second", [(f"sum by (job) (rate(dotnet_gc_collections_total[{RATE}]))", "{{job}}")], "ops"),
        ts("Thread pool queue length", [("sum by (job) (dotnet_thread_pool_queue_length_total)", "{{job}}")], "short"),
    ])

    d.row("Traces and logs")
    d.add({
        "type": "table",
        "title": "Recent API traces (Jaeger)",
        "datasource": JAEGER,
        "targets": [{"refId": "A", "datasource": JAEGER, "queryType": "search", "service": "opportunity-api", "limit": 20}],
        "options": {"showHeader": True},
        "fieldConfig": {"defaults": {}, "overrides": []},
    }, 0, d.y, 10, 10)
    d.add({
        "type": "logs",
        "title": "Logs (Loki)",
        "description": "OTLP logs of every opportuniTY service; each line links to its trace. No document content or query text (ADR-013 §7).",
        "datasource": LOKI,
        "targets": [{"refId": "A", "datasource": LOKI, "expr": '{service_name=~"opportunity-.+"}'}],
        "options": {"showTime": True, "wrapLogMessage": True, "sortOrder": "Descending", "enableLogDetails": True},
    }, 10, d.y, 14, 10)
    d.y += 10
    return d


# --- Search consistency and pipeline (E19-T05) ------------------------------------------------------------------------

def pipeline():
    d = Dashboard("opportunity-pipeline", "opportuniTY search consistency and pipeline",
                  "The SLO-driving signals of baseline §7, §21, §26 and §28 (E19-T05): commit to searchable, watermark, "
                  "outbox, queues, index tasks, jobs, search latency, WAL archiving and OpenSearch. Metric catalog and "
                  "alert runbooks: docs/operations/metrics.md and docs/operations/alerts.md.")

    d.row("Service level objectives")
    d.line([
        stat("Interactive commit → searchable p95",
             f'histogram_quantile(0.95, sum by (le) (rate(opportunity_search_commit_to_searchable_seconds_bucket{{opportunity_lane="interactive"}}[5m])))',
             "s", thresholds=limit(1), description="§17: < 1 s (alert InteractiveCommitToSearchableSlow)."),
        stat("Security projection lag p95",
             'histogram_quantile(0.95, sum by (le) (rate(opportunity_search_security_projection_lag_seconds_bucket[5m])))',
             "s", thresholds=limit(5), description="Q-10: ≤ 5 s (alert SecurityProjectionLagHigh)."),
        stat("Index lag (worst workspace)", 'max(opportunity_search_index_lag_seconds) or vector(0)', "s",
             thresholds=limit(60, 120), description="§26: bulk ≤ 2 min (alert BulkIndexLagHigh)."),
        stat("Outbox oldest age", 'max(opportunity_outbox_oldest_age_seconds) or vector(0)', "s",
             thresholds=limit(30, 60), description="ADR-001: ≤ 60 s (alert OutboxOldestAgeHigh)."),
        stat("Dead-lettered / parked messages",
             'sum(max by (messaging_destination_name) (opportunity_queue_depth{opportunity_queue_state=~"dlq|parking"})) or vector(0)',
             "short", thresholds=limit(1), description="0 (alert DeadLetterQueueNotEmpty)."),
        stat("WAL archive age", 'max(pg_stat_archiver_last_archive_age)', "s", thresholds=limit(120, 240),
             description="RPO ≤ 5 min; alerts above 4 min while WAL is written (WalArchiveLagHigh). Empty without archiving."),
    ], h=4)

    d.row("Search consistency")
    lanes = "opportunity_lane"
    d.line([
        ts("Commit → searchable p95 by lane",
           [(p95("opportunity_search_commit_to_searchable_seconds", lanes), "{{opportunity_lane}}")], "s",
           "opportunity.search.commit_to_searchable: interactive < 1 s, bulk < 2 min.", thresholds=1),
        ts("Security projection lag p95 by lane",
           [(p95("opportunity_search_security_projection_lag_seconds", lanes), "{{opportunity_lane}}")], "s",
           "opportunity.search.security_projection_lag (Q-10 ≤ 5 s).", thresholds=5),
        ts("Index lag by workspace",
           [("max by (opportunity_workspace_id) (opportunity_search_index_lag_seconds)", "{{opportunity_workspace_id}}")], "s",
           "Age of the oldest committed change not yet searchable (top workspaces + other).", thresholds=120),
    ])
    d.line([
        ts("Generations behind the watermark by workspace",
           [("max by (opportunity_workspace_id) (opportunity_search_generation_lag)", "{{opportunity_workspace_id}}")], "short",
           "opportunity.search.generation.lag = committed − indexed (§28). Must fall back to 0 (alert SearchWatermarkStuck)."),
        ts("Committed vs indexed generation",
           [("max by (opportunity_workspace_id) (opportunity_search_generation_committed)", "committed {{opportunity_workspace_id}}"),
            ("max by (opportunity_workspace_id) (opportunity_search_generation_indexed)", "indexed {{opportunity_workspace_id}}")],
           "short", "Per workspace (bounded)."),
        ts("Stale-version rejections per second",
           [(f"sum by (opportunity_lane) (rate(opportunity_search_stale_version_rejections_total[{RATE}]))", "{{opportunity_lane}}")],
           "ops", "OpenSearch external-version conflicts treated as no-ops (ADR-001 §3): expected under concurrent edits; a "
                  "sustained rise means duplicate or reordered work."),
    ])

    d.row("Outbox and dispatcher")
    d.line([
        ts("Outbox rows not applied", [("max by (opportunity_lane) (opportunity_outbox_pending)", "{{opportunity_lane}}")], "short"),
        ts("Outbox oldest undispatched age", [("max by (opportunity_lane) (opportunity_outbox_oldest_age_seconds)", "{{opportunity_lane}}")],
           "s", thresholds=60),
        ts("Commit → broker confirm p95", [(p95("opportunity_outbox_publish_latency_seconds", lanes), "{{opportunity_lane}}")], "s",
           "opportunity.outbox.publish_latency (outbox p95 < 100 ms at idle)."),
        ts("Published per second",
           [(f"sum by (opportunity_outcome) (rate(opportunity_dispatcher_published_total[{RATE}]))", "{{opportunity_outcome}}")],
           "ops", "unconfirmed = the broker did not confirm; the row is retried."),
    ])

    d.row("Queues")
    q = "messaging_destination_name"
    d.line([
        ts("Ready messages by queue",
           [(f'max by ({q}) (opportunity_queue_depth{{opportunity_queue_state="ready"}})', "{{messaging_destination_name}}")], "short"),
        ts("Unacknowledged messages by queue",
           [('sum by (queue) (rabbitmq_detailed_queue_messages_unacked)', "{{queue}}")], "short",
           "Delivered to a consumer and not yet acknowledged (rabbitmq_prometheus /metrics/detailed): in-flight work."),
        ts("Dead-letter and parking queues",
           [(f'max by ({q}) (opportunity_queue_depth{{opportunity_queue_state=~"dlq|parking"}})', "{{messaging_destination_name}}")],
           "short", "> 0 alerts; inspect the PostgreSQL copies (jobs dlq list), purge once settled.", thresholds=1),
        ts("Consumers by queue", [(f"max by ({q}) (opportunity_queue_consumers)", "{{messaging_destination_name}}")], "short",
           "0 on an index lane alerts (IndexQueueWithoutConsumers)."),
        ts("Dead letters recorded per second",
           [(f"sum by ({q}, error_type) (rate(opportunity_dlq_messages_total[{RATE}]))", "{{messaging_destination_name}} {{error_type}}")],
           "ops", "opportunity.dlq.messages by queue and reason."),
    ])

    d.row("Index tasks and jobs")
    d.line([
        ts("Index tasks by status",
           [("max by (opportunity_status, opportunity_lane) (opportunity_index_chunk_tasks)", "{{opportunity_status}} {{opportunity_lane}}")],
           "short", "IndexChunkTasks not yet Applied (Failed alerts: IndexChunkTasksFailed)."),
        ts("Oldest index task by status",
           [("max by (opportunity_status, opportunity_lane) (opportunity_index_chunk_task_oldest_age_seconds)", "{{opportunity_status}} {{opportunity_lane}}")],
           "s", "Running: since the lease began (≥ 15 min alerts); other statuses: since commit."),
        ts("Index task attempts per second",
           [(f"sum by (opportunity_lane, opportunity_outcome) (rate(opportunity_index_chunk_task_attempts_total[{RATE}]))", "{{opportunity_lane}} {{opportunity_outcome}}")],
           "ops", "retry and failed outcomes are chunk retries."),
        ts("Bulk items per second",
           [(f"sum by (opportunity_lane, opportunity_outcome) (rate(opportunity_index_bulk_items_total[{RATE}]))", "{{opportunity_lane}} {{opportunity_outcome}}")],
           "ops", "Documents written by _bulk: applied, stale, transient, permanent."),
    ])
    d.line([
        ts("Active jobs by type", [("max by (opportunity_job_type) (opportunity_jobs_active)", "{{opportunity_job_type}}")], "short", stack=True),
        ts("Open job chunks",
           [("max by (opportunity_job_type, opportunity_status) (opportunity_job_chunk_backlog)", "{{opportunity_job_type}} {{opportunity_status}}")],
           "short"),
        ts("Oldest open job chunk",
           [("max by (opportunity_job_type, opportunity_status) (opportunity_job_chunk_oldest_age_seconds)", "{{opportunity_job_type}} {{opportunity_status}}")],
           "s", "Running ≥ 15 min alerts (JobChunkRunningTooLong).", thresholds=900),
        ts("Job chunk outcomes per second",
           [(f"sum by (opportunity_job_type, opportunity_outcome) (rate(opportunity_job_chunks_total[{RATE}]))", "{{opportunity_job_type}} {{opportunity_outcome}}")],
           "ops", "retry = chunk retries; failed alerts (JobChunksFailed)."),
    ])

    d.row("Search")
    d.line([
        ts("Search latency by class",
           [(f'histogram_quantile({q_}, sum by (le, opportunity_search_class) (rate(opportunity_search_request_duration_seconds_bucket[{RATE}])))',
             f"p{int(q_ * 100)} {{{{opportunity_search_class}}}}") for q_ in (0.5, 0.95, 0.99)],
           "s", "§17: simple p95 < 1 s, complex p95 < 3 s (alert SearchLatencyHigh)."),
        ts("Searches per second by outcome",
           [(f"sum by (opportunity_search_class, opportunity_outcome) (rate(opportunity_search_request_duration_seconds_count[{RATE}]))", "{{opportunity_search_class}} {{opportunity_outcome}}")],
           "ops"),
        ts("Hits dropped by the page re-check",
           [(f"sum by (opportunity_search_drop_reason) (rate(opportunity_search_post_filter_dropped_total[{RATE}]))", "{{opportunity_search_drop_reason}}")],
           "ops", "Q-12 per-page PostgreSQL re-check; a rise follows security lag."),
    ])

    d.row("PostgreSQL")
    d.line([
        ts("WAL archive age", [("max by (instance) (pg_stat_archiver_last_archive_age)", "{{instance}}")], "s",
           "Seconds since the last WAL segment was archived (postgres_exporter stat_archiver). RPO ≤ 5 min.", thresholds=240),
        ts("WAL archive failures per second",
           [(f"sum by (instance) (rate(pg_stat_archiver_failed_count[{RATE}]))", "{{instance}}")], "ops", thresholds=0),
        ts("Replication lag", [("max by (instance) (pg_replication_lag_seconds)", "{{instance}}")], "s",
           "On standbys only (replication collector).", thresholds=60),
        ts("Rows changed per second",
           [(f'sum by (datname) (rate(pg_stat_database_tup_inserted{{datname!~"template.*"}}[{RATE}]) + rate(pg_stat_database_tup_updated{{datname!~"template.*"}}[{RATE}]) + rate(pg_stat_database_tup_deleted{{datname!~"template.*"}}[{RATE}]))', "{{datname}}")],
           "ops", "Write activity; WAL archiving is expected while this is above 0."),
    ])

    d.row("OpenSearch")
    d.line([
        ts("Heap used",
           [('max by (name) (elasticsearch_jvm_memory_used_bytes{area="heap"} / elasticsearch_jvm_memory_max_bytes{area="heap"})', "{{name}}")],
           "percentunit", "Above 90 % for 15 min alerts (OpenSearchHeapHigh).", thresholds=0.9),
        ts("Merges",
           [("sum by (name) (elasticsearch_indices_merges_current)", "running {{name}}"),
            (f"sum by (name) (rate(elasticsearch_indices_merges_total_time_seconds_total[{RATE}]))", "merge time/s {{name}}")],
           "short"),
        ts("Thread pool rejections per second",
           [(f'sum by (name, type) (rate(elasticsearch_thread_pool_rejected_count{{type=~"write|search"}}[{RATE}]))', "{{type}} {{name}}")],
           "ops", "write rejections alert (OpenSearchBulkRejections); index workers back off and retry."),
    ])
    return d


DASHBOARDS = [overview, pipeline]

if __name__ == "__main__":
    out = sys.argv[1] if len(sys.argv) > 1 else "dashboards"
    for build in DASHBOARDS:
        dashboard = build()
        with open(os.path.join(out, f"{dashboard.uid}.json"), "w", encoding="utf-8", newline="\n") as f:
            json.dump(dashboard.json(), f, indent=2, ensure_ascii=False)
            f.write("\n")
