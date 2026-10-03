using System.Globalization;
using System.Text;

using Opportunity.DataGenerator.Corpus.Model;
using Opportunity.DataGenerator.Corpus.Profiles;
using Opportunity.DataGenerator.Corpus.Randomness;

namespace Opportunity.DataGenerator.Corpus.Generation;

/// <summary>
/// Turns a <see cref="ChunkPlan"/> into numbered, fully annotated families. Pure function of (seed, profile, plan):
/// any chunk can be generated on any thread in any order and yields identical output.
/// </summary>
public sealed class ChunkGenerator
{
    private static readonly string[] EdocSources = ["FileShare", "SharePoint", "OneDrive", "Teams"];
    private static readonly string[] MailFolders = ["Inbox", "Sent Items", "Archive", "Deleted Items", "Inbox\\Projects", "Archive\\Old"];

    private readonly GenerationContext _ctx;
    private readonly CorpusProfile _profile;
    private readonly ContentFactory _content;

    public ChunkGenerator(GenerationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ctx = context;
        _profile = context.Profile;
        _content = new ContentFactory(context);
    }

    public GeneratedChunk Generate(ChunkPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var instances = new List<Instance>();
        var threads = new List<ThreadSummary>();
        foreach (UnitPlan unit in plan.Units)
        {
            BuildUnit(unit, instances, threads);
        }

        Rng.For(_ctx.Seed, StreamTag.ChunkShuffle, (ulong)plan.Index).Shuffle(instances);

        long docIndex = plan.FirstDocIndex;
        var families = new List<GeneratedFamily>(instances.Count);
        var groups = new Dictionary<UInt128, DuplicateGroup>();
        for (int ordinal = 0; ordinal < instances.Count; ordinal++)
        {
            Instance inst = instances[ordinal];
            Blueprint bp = inst.Blueprint;
            string custodian = _ctx.People.CustodianName(inst.CustodianId);
            var docs = new GeneratedDocument[bp.Nodes.Length];
            for (int j = 0; j < docs.Length; j++)
            {
                Node node = bp.Nodes[j];
                object?[] fields = (object?[])node.Content.Fields.Clone();
                fields[FieldCatalog.FilePath] = "\\" + custodian + "\\" + inst.Folder + "\\" + node.RelativePath;
                var doc = new GeneratedDocument
                {
                    Content = node.Content,
                    FamilySequence = j,
                    AttachmentDepth = node.Depth,
                    ParentSequence = node.Parent,
                    Custodian = custodian,
                    EmailThreadId = j == 0 ? bp.ThreadId : null,
                    Fields = fields,
                    DocIndex = docIndex,
                    ControlNumber = ControlNumber(docIndex),
                };
                docIndex++;
                docs[j] = doc;
            }

            string? familyDupId = bp.ExtraCopies > 0 ? bp.FamilyDuplicateGroupId : null;
            foreach (GeneratedDocument doc in docs)
            {
                doc.FamilyId = docs[0].ControlNumber;
                doc.ParentControlNumber = doc.ParentSequence < 0 ? null : docs[doc.ParentSequence].ControlNumber;
                doc.BegAttach = docs[0].ControlNumber;
                doc.EndAttach = docs[^1].ControlNumber;
                doc.FamilyDuplicateGroupId = familyDupId;
                Classify(doc, ordinal, groups);
            }

            families.Add(new GeneratedFamily
            {
                Documents = docs,
                UnitIndex = bp.UnitIndex,
                FamilyIndex = bp.FamilyIndex,
                CopyIndex = inst.CopyIndex,
                Custodian = custodian,
                EmailThreadId = bp.ThreadId,
                IsFiller = bp.IsFiller,
            });
        }

        foreach (DuplicateGroup group in groups.Values)
        {
            string[] all = [.. group.Custodians.Order(StringComparer.Ordinal)];
            string? groupId = group.Documents.Count > 1 ? "DG-" + group.Documents[0].Md5[..16].ToUpperInvariant() : null;
            foreach (GeneratedDocument doc in group.Documents)
            {
                doc.DuplicateGroupId = groupId;
                doc.AllCustodians = all;
                doc.DuplicateCustodians = all.Length == 1 ? [] : [.. all.Where(c => !string.Equals(c, doc.Custodian, StringComparison.Ordinal))];
            }
        }

        return new GeneratedChunk
        {
            Index = plan.Index,
            FirstDocIndex = plan.FirstDocIndex,
            Families = families,
            Threads = threads,
            UnitCount = plan.Units.Count,
        };
    }

    private static void Classify(GeneratedDocument doc, int familyOrdinal, Dictionary<UInt128, DuplicateGroup> groups)
    {
        UInt128 key = doc.Content.ContentKey;
        if (!groups.TryGetValue(key, out DuplicateGroup? group))
        {
            group = new DuplicateGroup();
            groups.Add(key, group);
            doc.DuplicateType = DuplicateType.None;
            doc.IsDuplicatePrimary = true;
        }
        else
        {
            doc.IsDuplicatePrimary = false;
            doc.DuplicateType = group.Families.Contains(familyOrdinal) ? DuplicateType.WithinFamily
                : group.Custodians.Contains(doc.Custodian) ? DuplicateType.ExactMd5
                : DuplicateType.CrossCustodian;
        }

        group.Documents.Add(doc);
        group.Custodians.Add(doc.Custodian);
        group.Families.Add(familyOrdinal);
    }

    private string ControlNumber(long docIndex) =>
        _profile.ControlNumberPrefix + (docIndex + 1).ToString("D" + _profile.ControlNumberDigits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    // ---- units ----------------------------------------------------------------------------------------------

    private void BuildUnit(UnitPlan unit, List<Instance> instances, List<ThreadSummary> threads)
    {
        var rng = Rng.For(_ctx.Seed, StreamTag.UnitContent, (ulong)unit.UnitIndex);
        int custodianId = _ctx.People.SampleCustodian(rng);
        if (unit.Kind != UnitKind.EmailConversation)
        {
            Blueprint bp = BuildEdocFamily(unit, unit.Families[0], custodianId);
            AddInstances(unit, bp, custodianId, [custodianId], instances);
            return;
        }

        int messages = unit.Families.Count;
        string threadId = string.Create(CultureInfo.InvariantCulture, $"TH-{StableHash.Of(_ctx.Seed, StreamTag.Thread, (ulong)unit.UnitIndex):X16}");
        threads.Add(new ThreadSummary(threadId, messages));

        // Core participants: the unit custodian plus 1..5 others, mostly internal.
        var participants = new List<int> { custodianId };
        int others = rng.NextInt(1, 5);
        for (int i = 0; i < others; i++)
        {
            int id = rng.Chance(0.6) ? _ctx.People.SampleInternal(rng) : _ctx.People.SampleExternal(rng);
            if (!participants.Contains(id))
            {
                participants.Add(id);
            }
        }

        int[] internalCustodians = [.. participants.Where(id => id < _ctx.People.CustodianCount)];
        string subject = _content.Decorate(rng, _content.Phrase(rng, 2, 7));
        byte[] rootIndex = new byte[22];
        rootIndex[0] = 0x01;
        new Rng(StableHash.Of(_ctx.Seed, StreamTag.ConversationIndex, (ulong)unit.UnitIndex)).NextBytes(rootIndex.AsSpan(1));

        int[] parent = new int[messages];
        bool[] hasReply = new bool[messages];
        string[] convIndex = new string[messages];
        string[] messageIds = new string[messages];
        DateTimeOffset[] sent = new DateTimeOffset[messages];
        ulong[] bodySeeds = new ulong[messages];
        long[] bodyBytes = new long[messages];
        Person sender0 = _ctx.People[rng.Pick(participants)];
        DateTimeOffset previous = _content.SampleDate(rng, sender0.Offset);
        for (int m = 0; m < messages; m++)
        {
            parent[m] = m == 0 ? -1 : (m >= 2 && rng.Chance(_profile.Threads.BranchProbability) ? rng.NextInt(m - 1) : m - 1);
            if (parent[m] >= 0)
            {
                hasReply[parent[m]] = true;
                previous = previous.AddSeconds(Math.Round(Math.Min(rng.Exponential(_profile.Threads.MeanReplyGapHours) * 3600, 90 * 86400.0)));
            }

            sent[m] = previous;
            convIndex[m] = m == 0 ? Convert.ToHexString(rootIndex)
                : convIndex[parent[m]] + StableHash.Of(_ctx.Seed, StreamTag.ConversationIndex, (ulong)unit.UnitIndex, (ulong)m).ToString("X10", CultureInfo.InvariantCulture)[^10..];
        }

        for (int m = 0; m < messages; m++)
        {
            var thread = new ThreadInfo(
                threadId,
                m,
                parent[m],
                subject,
                participants,
                convIndex[m],
                sent[m],
                parent[m] >= 0 ? messageIds[parent[m]] : null,
                parent[m] >= 0 ? bodySeeds[parent[m]] : 0,
                parent[m] >= 0 ? bodyBytes[parent[m]] : 0,
                IsLeaf: !hasReply[m]);
            Blueprint bp = BuildEmailFamily(unit, m, unit.Families[m], custodianId, thread);
            messageIds[m] = (string)bp.Nodes[0].Content.Fields[FieldCatalog.MessageId]!;
            bodySeeds[m] = bp.Nodes[0].Content.Text.BodySeed;
            bodyBytes[m] = bp.Nodes[0].Content.Text.BodyBytes;
            AddInstances(unit, bp, custodianId, internalCustodians, instances);
        }
    }

    private void AddInstances(UnitPlan unit, Blueprint bp, int originalCustodian, int[] participantCustodians, List<Instance> instances)
    {
        var rng = Rng.For(_ctx.Seed, StreamTag.Copies, (ulong)unit.UnitIndex, (ulong)bp.FamilyIndex);
        for (int copy = 0; copy <= bp.ExtraCopies; copy++)
        {
            int custodian = originalCustodian;
            if (copy > 0 && !rng.Chance(_profile.Duplicates.SameCustodianShare) && _ctx.People.CustodianCount > 1)
            {
                int[] candidates = [.. participantCustodians.Where(c => c != originalCustodian)];
                if (candidates.Length > 0 && rng.Chance(_profile.Custodians.CopyToParticipantProbability))
                {
                    custodian = rng.Pick(candidates);
                }
                else
                {
                    for (int attempt = 0; attempt < 16 && custodian == originalCustodian; attempt++)
                    {
                        custodian = _ctx.People.SampleCustodian(rng);
                    }

                    if (custodian == originalCustodian)
                    {
                        custodian = (originalCustodian + 1) % _ctx.People.CustodianCount;
                    }
                }
            }

            string folder = bp.IsEmail
                ? "Mailbox\\" + rng.Pick(MailFolders)
                : "Documents\\" + _content.Phrase(rng, 1, 2, capitalize: true).Replace(' ', '\\');
            instances.Add(new Instance(bp, copy, custodian, folder));
        }
    }

    // ---- families -------------------------------------------------------------------------------------------

    private Blueprint BuildEdocFamily(UnitPlan unit, FamilyPlan plan, int custodianId)
    {
        var rng = Rng.For(_ctx.Seed, StreamTag.FamilyContent, (ulong)unit.UnitIndex, 0);
        string rootType = plan.IsContainer ? "zip" : ContentFactory.SampleTopLevelType(rng);
        Tree tree = BuildTree(rng, plan, isEmail: false, rootType);
        Person author = _ctx.People[rng.Chance(0.7) ? custodianId : _ctx.People.SampleAnyone(rng)];
        DateTimeOffset? created = rng.Chance(_profile.Dates.MissingEdocDateShare) ? null : _content.SampleDate(rng, author.Offset);
        string source = rng.Pick(EdocSources);
        Node[] nodes = BuildNodes(unit, 0, rng, tree, created ?? _content.SampleDate(rng, author.Offset), source, default, (r, fields, type, key, depth) =>
        {
            FillEdocFields(r, fields, type, depth == 0 ? author : null, depth == 0 ? created : null);
            return null;
        });

        return new Blueprint(unit.UnitIndex, 0, nodes, plan.ExtraCopies, null, IsEmail: false, IsFiller: unit.Kind == UnitKind.Filler,
            FamilyKey(unit.UnitIndex, 0));
    }

    private Blueprint BuildEmailFamily(UnitPlan unit, int familyIndex, FamilyPlan plan, int custodianId, ThreadInfo thread)
    {
        var rng = Rng.For(_ctx.Seed, StreamTag.FamilyContent, (ulong)unit.UnitIndex, (ulong)familyIndex);
        Tree tree = BuildTree(rng, plan, isEmail: true, "msg");
        var quoted = new QuotedSource(thread.ParentBodySeed, thread.ParentBodyBytes);
        Node[] nodes = BuildNodes(unit, familyIndex, rng, tree, thread.Sent, "Exchange", quoted, (r, fields, type, key, depth) =>
            depth == 0
                ? FillThreadEmailFields(r, fields, key, custodianId, thread, hasAttachments: tree.Count > 1)
                : type == "msg" ? FillEmbeddedEmailFields(r, fields, key) : FillEdocFields(r, fields, type, null, null));

        return new Blueprint(unit.UnitIndex, familyIndex, nodes, plan.ExtraCopies, thread.ThreadId, IsEmail: true, IsFiller: false,
            FamilyKey(unit.UnitIndex, familyIndex));
    }

    private string FamilyKey(long unit, int family) =>
        string.Create(CultureInfo.InvariantCulture, $"FDG-{StableHash.Of(_ctx.Seed, StreamTag.FamilyKey, (ulong)unit, (ulong)family):X16}");

    private delegate string? FieldFiller(Rng rng, object?[] fields, string fileType, UInt128 key, int depth);

    /// <summary>Creates node contents in pre-order. The filler returns the email header used as text prefix (or null).</summary>
    private Node[] BuildNodes(UnitPlan unit, int familyIndex, Rng rng, Tree tree, DateTimeOffset familyDate, string source, QuotedSource quoted, FieldFiller fill)
    {
        var nodes = new Node[tree.Count];
        for (int i = 0; i < tree.Count; i++)
        {
            UInt128 key = _content.ContentKey(unit.UnitIndex, familyIndex, i);
            var docRng = Rng.For(_ctx.Seed, StreamTag.DocumentContent, (ulong)unit.UnitIndex, (ulong)familyIndex, (ulong)i);
            string type = tree.Types[i];
            bool isEmail = type == "msg";
            var fields = new object?[_ctx.Catalog.Count];
            long textBytes = _content.SampleTextBytes(docRng, type);
            fields[FieldCatalog.SourceSystem] = source;
            if (i > 0)
            {
                // Attachments predate the parent.
                DateTimeOffset created = familyDate.AddSeconds(-Math.Round(Math.Min(docRng.Exponential(30 * 86400.0), 3650 * 86400.0)));
                fields[FieldCatalog.DateCreated] = created;
                fields[FieldCatalog.DateLastModified] = created.AddSeconds(Math.Round(Math.Min(docRng.Exponential(5 * 86400.0), (familyDate - created).TotalSeconds)));
            }

            string? prefix = fill(docRng, fields, type, key, tree.Depth[i]);
            _content.FillCommonFields(docRng, fields, type, textBytes, isEmail);
            string fileName = fields[FieldCatalog.FileName] as string ?? _content.Decorate(docRng, _content.Phrase(docRng, 1, 4).Replace(' ', '_'), fileName: true) + "." + type;
            fields[FieldCatalog.FileName] = fileName;

            QuotedSource q = i == 0 ? quoted : default;
            var (text, cluster, needles, proximity) = _content.BuildText(docRng, key, textBytes, prefix, q.Seed, q.Bytes, !isEmail, tree.Depth[i]);
            var (md5, sha) = ContentFactory.Hashes(key);
            var content = new DocumentContent
            {
                ContentKey = key,
                Kind = isEmail ? DocumentKind.Email : DocumentKind.EDocument,
                FileType = type,
                Md5 = md5,
                Sha256 = sha,
                Text = text,
                Fields = fields,
                NearDuplicateClusterId = cluster,
                Needles = needles,
                ProximityHits = proximity,
            };
            string relative = i == 0 ? fileName : nodes[tree.Parent[i]].RelativePath + "\\" + fileName;
            nodes[i] = new Node(content, tree.Depth[i], tree.Parent[i], relative);
        }

        ApplyWithinFamilyDuplicate(rng, tree, nodes);
        return nodes;
    }

    private void ApplyWithinFamilyDuplicate(Rng rng, Tree tree, Node[] nodes)
    {
        if (tree.Count < 3 || !rng.Chance(_profile.Duplicates.WithinFamilyRate))
        {
            return;
        }

        var leaves = new List<int>();
        for (int i = 1; i < tree.Count; i++)
        {
            if (!tree.HasChildren[i])
            {
                leaves.Add(i);
            }
        }

        if (leaves.Count < 2)
        {
            return;
        }

        int t = 1 + rng.NextInt(leaves.Count - 1);
        int s = rng.NextInt(t);
        Node source = nodes[leaves[s]];
        Node target = nodes[leaves[t]];
        string name = (string)source.Content.Fields[FieldCatalog.FileName]!;
        string relative = target.Parent == 0 ? nodes[0].RelativePath + "\\" + name : nodes[target.Parent].RelativePath + "\\" + name;
        nodes[leaves[t]] = target with { Content = source.Content, RelativePath = relative };
    }

    private Tree BuildTree(Rng rng, FamilyPlan plan, bool isEmail, string rootType)
    {
        int n = 1 + plan.ChildCount;
        int maxDepth = _profile.Families.MaxAttachmentDepth;
        double nest = _profile.Families.NestingProbability;
        int[] parent = new int[n];
        int[] depth = new int[n];
        parent[0] = -1;
        for (int i = 1; i < n; i++)
        {
            int p = 0;
            if (plan.IsContainer && isEmail && i >= 2)
            {
                p = 1;
                if (rng.Chance(nest))
                {
                    int j = 1 + rng.NextInt(i - 1);
                    p = depth[j] < maxDepth ? j : 1;
                }
            }
            else if (i >= 2 && rng.Chance(nest))
            {
                int j = 1 + rng.NextInt(i - 1);
                p = depth[j] < maxDepth ? j : 0;
            }

            if (depth[p] >= maxDepth)
            {
                p = 0;
            }

            parent[i] = p;
            depth[i] = depth[p] + 1;
        }

        // Re-order to pre-order (parent first, then each attachment followed by its own descendants).
        var children = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            children[i] = [];
        }

        for (int i = 1; i < n; i++)
        {
            children[parent[i]].Add(i);
        }

        int[] order = new int[n];
        int[] position = new int[n];
        int count = 0;
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            int v = stack.Pop();
            position[v] = count;
            order[count++] = v;
            for (int c = children[v].Count - 1; c >= 0; c--)
            {
                stack.Push(children[v][c]);
            }
        }

        var tree = new Tree(n);
        for (int k = 0; k < n; k++)
        {
            int v = order[k];
            tree.Parent[k] = v == 0 ? -1 : position[parent[v]];
            tree.Depth[k] = depth[v];
            tree.HasChildren[k] = children[v].Count > 0;
        }

        for (int k = 0; k < n; k++)
        {
            string type = k == 0 ? rootType
                : plan.IsContainer && isEmail && order[k] == 1 ? "zip"
                : ContentFactory.SampleAttachmentType(rng);
            if (k > 0 && tree.HasChildren[k] && type is not ("zip" or "msg" or "docx" or "xlsx" or "pptx"))
            {
                type = rng.Chance(0.5) ? "zip" : "msg";
            }

            tree.Types[k] = type;
        }

        return tree;
    }

    // ---- metadata -------------------------------------------------------------------------------------------

    private string? FillEdocFields(Rng rng, object?[] fields, string type, Person? author, DateTimeOffset? created)
    {
        if (author != null)
        {
            fields[FieldCatalog.Author] = author.DisplayName;
            fields[FieldCatalog.DateCreated] = created;
            fields[FieldCatalog.DateLastModified] = created?.AddSeconds(Math.Round(Math.Min(rng.Exponential(20 * 86400.0), 365 * 86400.0)));
        }
        else if (rng.Chance(0.6))
        {
            fields[FieldCatalog.Author] = _ctx.People[_ctx.People.SampleAnyone(rng)].DisplayName;
        }

        if (type is "docx" or "pptx" or "pdf" or "xlsx")
        {
            if (rng.Chance(0.6))
            {
                fields[FieldCatalog.Title] = _content.Decorate(rng, _content.Phrase(rng, 2, 8));
            }

            fields[FieldCatalog.HasHiddenContent] = rng.Chance(0.05);
        }

        return null;
    }

    private string? FillEmbeddedEmailFields(Rng rng, object?[] fields, UInt128 key)
    {
        Person from = _ctx.People[_ctx.People.SampleAnyone(rng)];
        DateTimeOffset sent = _content.SampleDate(rng, from.Offset);
        string subject = _content.Decorate(rng, _content.Phrase(rng, 2, 6));
        string[] to = _content.Recipients(rng, rng.NextInt(1, 5), [], from.Id);
        FillEmailHeader(rng, fields, from, to, [], [], subject, sent, from.Offset, MessageId(key, from));
        return null;
    }

    private string FillThreadEmailFields(Rng rng, object?[] fields, UInt128 key, int custodianId, ThreadInfo thread, bool hasAttachments)
    {
        IReadOnlyList<int> participants = thread.Participants;
        Person from = _ctx.People[participants[rng.NextInt(participants.Count)]];
        int toCount = _content.SampleToCount(rng);
        string[] to = _content.Recipients(rng, toCount, participants, from.Id);
        string[] cc = _content.Recipients(rng, _content.SampleCopyCount(rng, _profile.Recipients.CcEmptyShare), [], from.Id);
        string[] bcc = _content.Recipients(rng, _content.SampleCopyCount(rng, _profile.Recipients.BccEmptyShare), [], from.Id);
        Person mailbox = _ctx.People[custodianId];
        if (from.Id != custodianId && !to.Contains(mailbox.Formatted) && !cc.Contains(mailbox.Formatted) && !bcc.Contains(mailbox.Formatted))
        {
            to = [.. to, mailbox.Formatted];
        }

        string subject = thread.MessageNumber == 0 ? thread.Subject
            : (rng.Chance(_profile.Threads.ForwardProbability) ? "FW: " : "RE: ") + thread.Subject;
        string messageId = MessageId(key, from);
        FillEmailHeader(rng, fields, from, to, cc, bcc, subject, thread.Sent, mailbox.Offset, messageId);
        fields[FieldCatalog.InReplyTo] = thread.ParentMessageId;
        fields[FieldCatalog.ConversationIndex] = thread.ConversationIndex;
        fields[FieldCatalog.InclusiveEmail] = thread.IsLeaf || hasAttachments;

        var header = new StringBuilder();
        header.Append("From: ").Append(from.Formatted).Append('\n');
        header.Append("To: ").AppendJoin("; ", to.Take(10));
        if (to.Length > 10)
        {
            header.Append(CultureInfo.InvariantCulture, $"; (+{to.Length - 10} more)");
        }

        header.Append('\n');
        if (cc.Length > 0)
        {
            header.Append("CC: ").AppendJoin("; ", cc.Take(10)).Append('\n');
        }

        header.Append("Sent: ").Append(thread.Sent.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)).Append('\n');
        header.Append("Subject: ").Append(subject).Append("\n\n");
        return header.ToString();
    }

    private static void FillEmailHeader(Rng rng, object?[] fields, Person from, string[] to, string[] cc, string[] bcc, string subject,
        DateTimeOffset sent, TimeSpan receiverOffset, string messageId)
    {
        fields[FieldCatalog.From] = from.Formatted;
        fields[FieldCatalog.To] = to;
        fields[FieldCatalog.Cc] = cc.Length > 0 ? cc : null;
        fields[FieldCatalog.Bcc] = bcc.Length > 0 ? bcc : null;
        fields[FieldCatalog.Subject] = subject;
        fields[FieldCatalog.DateSent] = sent;
        fields[FieldCatalog.DateReceived] = sent.AddSeconds(rng.NextInt(2, 600)).ToOffset(receiverOffset);
        fields[FieldCatalog.TimeZone] = ContentFactory.FormatOffset(sent.Offset);
        fields[FieldCatalog.MessageId] = messageId;
        fields[FieldCatalog.Importance] = rng.NextInt(100) switch { < 5 => "Low", < 90 => "Normal", _ => "High" };
        string safeSubject = subject.Length > 60 ? subject[..60] : subject;
        fields[FieldCatalog.FileName] = safeSubject.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Replace('\\', '_') + ".msg";
    }

    private string MessageId(UInt128 key, Person from) =>
        string.Create(CultureInfo.InvariantCulture, $"<{StableHash.Of(_ctx.Seed, StreamTag.MessageId, (ulong)key, (ulong)(key >> 64)):x16}@{from.Address[(from.Address.IndexOf('@', StringComparison.Ordinal) + 1)..]}>");

    // ---- private types --------------------------------------------------------------------------------------

    private readonly record struct QuotedSource(ulong Seed, long Bytes);

    private sealed record Node(DocumentContent Content, int Depth, int Parent, string RelativePath);

    private sealed record Blueprint(long UnitIndex, int FamilyIndex, Node[] Nodes, int ExtraCopies, string? ThreadId, bool IsEmail, bool IsFiller, string FamilyDuplicateGroupId);

    private sealed record Instance(Blueprint Blueprint, int CopyIndex, int CustodianId, string Folder);

    private sealed record ThreadInfo(
        string ThreadId,
        int MessageNumber,
        int ParentMessage,
        string Subject,
        IReadOnlyList<int> Participants,
        string ConversationIndex,
        DateTimeOffset Sent,
        string? ParentMessageId,
        ulong ParentBodySeed,
        long ParentBodyBytes,
        bool IsLeaf);

    private sealed class Tree(int count)
    {
        public int Count { get; } = count;

        public int[] Parent { get; } = new int[count];

        public int[] Depth { get; } = new int[count];

        public bool[] HasChildren { get; } = new bool[count];

        public string[] Types { get; } = new string[count];
    }

    private sealed class DuplicateGroup
    {
        public List<GeneratedDocument> Documents { get; } = [];

        public HashSet<string> Custodians { get; } = new(StringComparer.Ordinal);

        public HashSet<int> Families { get; } = [];
    }
}
