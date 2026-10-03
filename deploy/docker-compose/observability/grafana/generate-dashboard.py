"""Generates the starter dashboard. Edit this file, not the JSON, then run from this directory:

    python3 generate-dashboard.py dashboards/opportunity-overview.json

Metric names are the Prometheus translations of the OpenTelemetry names (ADR-017 §5): dots become underscores and
the unit/`_total` suffixes are appended, e.g. `http.server.request.duration` -> `http_server_request_duration_seconds`.
"""
import json
import sys

PROM = {"type": "prometheus", "uid": "prometheus"}
LOKI = {"type": "loki", "uid": "loki"}
JAEGER = {"type": "jaeger", "uid": "jaeger"}

panels = []
next_id = 1


def add(panel, x, y, w, h):
    global next_id
    panel["id"] = next_id
    next_id += 1
    panel["gridPos"] = {"x": x, "y": y, "w": w, "h": h}
    panels.append(panel)


def row(title, y):
    add({"type": "row", "title": title, "collapsed": False, "panels": []}, 0, y, 24, 1)


def ts(title, targets, unit, description="", stack=False):
    return {
        "type": "timeseries",
        "title": title,
        "description": description,
        "datasource": PROM,
        "fieldConfig": {
            "defaults": {
                "unit": unit,
                "custom": {"lineWidth": 1, "fillOpacity": 10, "showPoints": "never",
                           "stacking": {"mode": "normal" if stack else "none"}},
            },
            "overrides": [],
        },
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


API = 'job="opportunity/opportunity-api"'
RATE = "$__rate_interval"

y = 0
row("Services", y); y += 1
add(stat("Targets up", 'up', "none", "{{job}}",
         mappings=[{"type": "value", "options": {"0": {"text": "down", "color": "red"}, "1": {"text": "up", "color": "green"}}}],
         description="Scraped infrastructure exporters and the collector."), 0, y, 12, 4)
add(stat("Worker heartbeat age", 'max by (worker_type) (opportunity_worker_heartbeat_age_seconds)', "s", "{{worker_type}}",
         thresholds={"mode": "absolute", "steps": [{"color": "green", "value": None}, {"color": "orange", "value": 60}, {"color": "red", "value": 120}]},
         description="opportunity.worker.heartbeat.age per worker type; > 2 heartbeat intervals means a stuck module."), 12, y, 12, 4)
y += 4

row("API (opportunity-api)", y); y += 1
add(ts("Requests per second by route",
       [(f'sum by (http_route, http_request_method) (rate(http_server_request_duration_seconds_count{{{API}}}[{RATE}]))', "{{http_request_method}} {{http_route}}")],
       "reqps"), 0, y, 8, 8)
add(ts("Latency p95 by route",
       [(f'histogram_quantile(0.95, sum by (le, http_route) (rate(http_server_request_duration_seconds_bucket{{{API}}}[{RATE}])))', "{{http_route}}")],
       "s", "Baseline §17: simple search p95 < 1 s, complex < 3 s."), 8, y, 8, 8)
add(ts("Error ratio (5xx)",
       [(f'(sum(rate(http_server_request_duration_seconds_count{{{API}, http_response_status_code=~"5.."}}[{RATE}])) or vector(0)) / clamp_min(sum(rate(http_server_request_duration_seconds_count{{{API}}}[{RATE}])), 1e-9)', "5xx")],
       "percentunit", "§17 API availability target 99.9%."), 16, y, 8, 8)
y += 8

row("Workers and messaging", y); y += 1
add(ts("Messages processed per second",
       [(f'sum by (worker_type, messaging_destination_name) (rate(messaging_client_consumed_messages_total[{RATE}]))', "{{worker_type}} {{messaging_destination_name}}")],
       "ops", "messaging.client.consumed.messages; populated once the E06/E07 consumers use WorkerTelemetry."), 0, y, 8, 8)
add(ts("Message processing p95",
       [(f'histogram_quantile(0.95, sum by (le, worker_type) (rate(messaging_process_duration_seconds_bucket[{RATE}])))', "{{worker_type}}")],
       "s"), 8, y, 8, 8)
add(ts("RabbitMQ messages (ready / unacked)",
       [("sum(rabbitmq_queue_messages_ready)", "ready"), ("sum(rabbitmq_queue_messages_unacked)", "unacked")],
       "short", "From the rabbitmq_prometheus plugin. Per-queue depth and DLQ alerts arrive with E19-T05."), 16, y, 8, 8)
y += 8

row("PostgreSQL and OpenSearch", y); y += 1
add(ts("PostgreSQL client operation p95 (Npgsql)",
       [(f'histogram_quantile(0.95, sum by (le, job) (rate(db_client_operation_duration_seconds_bucket[{RATE}])))', "{{job}}")],
       "s"), 0, y, 8, 8)
add(ts("PostgreSQL backends",
       [('sum by (datname) (pg_stat_database_numbackends{datname!~"template.*"})', "{{datname}}")],
       "short", "postgres_exporter."), 8, y, 8, 8)
add(ts("OpenSearch JVM heap used",
       [('sum by (name) (elasticsearch_jvm_memory_used_bytes{area="heap"})', "{{name}}")],
       "bytes", "elasticsearch_exporter against the product cluster (metrics only; logs never go to OpenSearch, Q-42)."), 16, y, 8, 8)
y += 8

row(".NET runtime", y); y += 1
add(ts("Working set",
       [("sum by (job) (dotnet_process_memory_working_set_bytes)", "{{job}}")], "bytes"), 0, y, 8, 8)
add(ts("GC collections per second",
       [(f"sum by (job) (rate(dotnet_gc_collections_total[{RATE}]))", "{{job}}")], "ops"), 8, y, 8, 8)
add(ts("Thread pool queue length",
       [("sum by (job) (dotnet_thread_pool_queue_length_total)", "{{job}}")], "short"), 16, y, 8, 8)
y += 8

row("Traces and logs", y); y += 1
add({
    "type": "table",
    "title": "Recent API traces (Jaeger)",
    "datasource": JAEGER,
    "targets": [{"refId": "A", "datasource": JAEGER, "queryType": "search", "service": "opportunity-api", "limit": 20}],
    "options": {"showHeader": True},
    "fieldConfig": {"defaults": {}, "overrides": []},
}, 0, y, 10, 10)
add({
    "type": "logs",
    "title": "Logs (Loki)",
    "description": "OTLP logs of every opportuniTY service; each line links to its trace. No document content or query text (ADR-013 §7).",
    "datasource": LOKI,
    "targets": [{"refId": "A", "datasource": LOKI, "expr": '{service_name=~"opportunity-.+"}'}],
    "options": {"showTime": True, "wrapLogMessage": True, "sortOrder": "Descending", "enableLogDetails": True},
}, 10, y, 14, 10)

dashboard = {
    "uid": "opportunity-overview",
    "title": "opportuniTY overview",
    "description": "Starter dashboard (E19-T04). Pipeline, consistency and SLO dashboards arrive with E19-T05.",
    "tags": ["opportunity"],
    "timezone": "browser",
    "schemaVersion": 41,
    "version": 1,
    "editable": False,
    "refresh": "30s",
    "time": {"from": "now-1h", "to": "now"},
    "templating": {"list": []},
    "annotations": {"list": []},
    "links": [],
    "panels": panels,
}

json.dump(dashboard, open(sys.argv[1], "w"), indent=2)
open(sys.argv[1], "a").write("\n")
