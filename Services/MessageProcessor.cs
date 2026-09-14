using Cheing;
using Cheing.Net.Ai;
using EMF.FilerSvc;
using EMF.FilerSvc.Models;
using EMF.Mail.Models;
using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FilerDataService = EMF.FilerSvc.Services.DataService;

namespace EMF.Mail.Services
{
    // Single processor for every app -- TriageService and MsgTypeProcessor are both retired. There is
    // no more per-AppId branching anywhere here: whether an account gets sender-approval gating is read
    // from MailAccount.GetSenderHistHndName (already a per-account config value), and whether a package
    // comes from a document or from Fields alone is decided per PackageInstance by whether it has an
    // IsPackage attachment -- both signals already in the data, no hardcoded AppId check needed.
    public partial class MessageProcessor(DataService mailDataSvc, FilerDataService filerDataSvc, CommandService cmdSvc, ClaudeClassifier classifier, Filer filer, ConversationService conv)
    {
        [GeneratedRegex(@"\s+")]
        private static partial Regex WhitespaceRegex();
        private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

        #region Public methods. Main entry point from console app.
        public async Task ProcessAccountAsync(MailAccount account, IMailService mail)
        // mail is supplied by the caller (Program.cs), constructed there per account's ProvCode -- this
        // method doesn't know or care which provider implementation it's talking to.
        //
        // Changed messages are fetched FIRST -- the account's msgtypes/doctypes/field lists are only
        // fetched at all when there's actually mail to process, since an empty poll (the overwhelming
        // majority of runs) previously paid for that config every single cycle for no reason. The three
        // fetches that are needed are independent of each other, so they run via Task.WhenAll rather than
        // sequential awaits.
        {
            var (messages, deltaLink) = await mail.GetChangedMessagesAsync(account.LastMsgLink);
            Tracker.Track($"Account {account.AcctName}: {messages.Count} message(s) fetched.");

            if (messages.Count > 0)
            {
                var processDocTypesTask = filerDataSvc.GetProcessAndDocTypesAsync(account.AppId);
                var msgTypesTask = filerDataSvc.GetMsgTypesAsync(account.AppId);
                var msgTypeFieldsTask = filerDataSvc.GetMsgTypeFieldsAsync(account.AppId);

                await Task.WhenAll(processDocTypesTask, msgTypesTask, msgTypeFieldsTask);

                var (process, docTypes) = processDocTypesTask.Result;
                var msgTypes = msgTypesTask.Result;
                var fieldsByMsgType = msgTypeFieldsTask.Result
                    .GroupBy(f => f.MsgTpId)
                    .ToDictionary(g => g.Key, g => g.Cast<ClaudeFieldSpec>().ToList());

                foreach (var message in messages)
                {
                    try
                    {
                        if (string.Equals(message.FromAddr, account.AcctName, StringComparison.OrdinalIgnoreCase))
                        {
                            await ProcessSelfSentMessageAsync(account, message);
                        }
                        else if (string.Equals(message.FromAddr, account.AdmAcctEMail, StringComparison.OrdinalIgnoreCase))
                        {
                            await ProcessAdminReplyAsync(account, mail, message, msgTypes, fieldsByMsgType);
                        }
                        else
                        {
                            // Checked before normal classification -- a reply to an open RFI takes this path
                            // instead of being triaged again as a fresh Submission/Inquiry.
                            var rfiBridge = (await mailDataSvc.GetRfiBridgeAsync(message.BridgeMsgIds)).FirstOrDefault();

                            if (rfiBridge is not null)
                                await ProcessInfoRequestReplyAsync(account, mail, message, rfiBridge, docTypes, process?.ProcessDesc ?? "");
                            else
                                await ProcessInboundMessageAsync(account, mail, docTypes, msgTypes, fieldsByMsgType, process?.ProcessDesc ?? "", message);
                        }
                    }
                    catch (Exception ex)
                    {
                        Tracker.Track($"{message.FromAddr}: unhandled exception processing message ({ex.Message}).");
                    }
                }
            }

            var pollResult = await mailDataSvc.SaveMessageBookmarkAsync(new AccountBookmark { AcctId = account.AcctId, LastPollDT = DateTime.UtcNow, LastMsgLink = deltaLink });
            if (pollResult.IsFailure)
                Tracker.Track($"Account {account.AcctName}: failed to update LastPollDT/LastMsgLink ({pollResult.Message}).");
        }
        #endregion

        #region Private helper methods
        private static object? GetFieldValue(Dictionary<string, object>? fields, string? key)
        {
            if (fields is null || key is null || !fields.TryGetValue(key, out var value)) return null;
            return value is JsonElement je && je.ValueKind == JsonValueKind.Null ? null : value;
        }
        private static string? GetFieldString(Dictionary<string, object>? fields, string? key)
        {
            var value = GetFieldValue(fields, key);
            return value switch { null => null, JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() : je.ToString(), _ => value.ToString() };
        }
        private static int? GetFieldInt(Dictionary<string, object>? fields, string? key)
        {
            var value = GetFieldValue(fields, key);
            return value switch
            {
                null => null,
                JsonElement je when je.ValueKind == JsonValueKind.Number => je.TryGetInt32(out var i) ? i : null,
                JsonElement je when je.ValueKind == JsonValueKind.String => int.TryParse(je.GetString(), out var i) ? i : null,
                JsonElement => null,
                int i2 => i2,
                long l => (int)l,
                _ => int.TryParse(value.ToString(), out var p) ? p : null
            };
        }
        private static string GetInfoRequestBody(List<PkgTask> gaps) => "We still need additional documentation before your request can be processed: " + string.Join("; ", gaps.Select(g => g.Task)) + ".";
        private static string GetPlainText(string html)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var junk = doc.DocumentNode.SelectNodes("//script|//style|//head");
            if (junk is not null)
                foreach (var node in junk)
                    node.Remove();

            var decoded = System.Net.WebUtility.HtmlDecode(doc.DocumentNode.InnerText);
            return WhitespaceRegex().Replace(decoded, " ").Trim();
        }
        private static string GetApprovalComment(ClassifyResult result, ClaudeFieldSpec linkField, bool isKnownSender, List<string> missingInfo)
        // Builds the admin-facing comment for a hold/approval-request email -- echoes what was already
        // extracted (so approving isn't a blind "trust the sender" click) and calls out anything still
        // missing, so one reply can both decide and supply the gap instead of a second round-trip.
        // missingInfo is passed in rather than recomputed -- the caller already needed it for MsgResult.
        // linkField.Parameter (e.g. "VendName") is the Searchable field's paired display-name field.
        {
            var fields = result.Packages.FirstOrDefault()?.Fields;
            var name = linkField.Parameter is not null ? GetFieldString(fields, linkField.Parameter) : null;

            var lines = new List<string>
            {
                name is not null
                    ? $"This sender is not yet linked to any {linkField.FieldName}. It looks like it may be from \"{name}\" -- reply APPROVE to link it, REJECT to decline, or give the correct name."
                    : $"This sender is not yet linked to any {linkField.FieldName} and none could be determined from the message. Reply with the correct name to link it to, or REJECT to decline."
            };

            if (result.Packages.Any(p => p.Attachments.Any(a => a.IsPackage)) && !isKnownSender)
                lines.Add("Attachments have not been opened yet -- I need to confirm this sender is allowed to submit first. They'll be processed once approved.");

            lines.AddRange(missingInfo.Select(g => char.ToUpper(g[0]) + g[1..] + "."));

            return string.Join(" ", lines);
        }
        private static List<string> GetMissingInfo(ClassifyResult result, ClaudeFieldSpec linkField, bool isKnownSender)
        // The specific gaps in a classification that should be flagged and drive PARTIAL vs HELD -- terse,
        // no MsgNo/sender/reference numbers (those are already columns on the same row). The link field
        // (e.g. VendId) only counts as a gap when the sender is known (isKnownSender): for a first-contact
        // sender, an unresolved link value is the deliberate outcome of not opening the attachment yet, not
        // something missing.
        {
            var gaps = new List<string>();

            if (result.MsgTpCode is null)
                gaps.Add("message type could not be determined");
            else if (result.Packages.Count == 0)
                gaps.Add("no packages could be identified for processing");

            var linkValue = GetFieldValue(result.Packages.FirstOrDefault()?.Fields, linkField.FieldName);
            if (isKnownSender && linkValue is null)
                gaps.Add($"{linkField.FieldName} could not be confirmed even after reviewing the attachment");

            return gaps;
        }
        #endregion

        #region Private processing methods
        private async Task FinalizeAsync(MessageFinalization finalize)
        // Every terminal write goes through here instead of calling mailDataSvc.FinalizeMessageAsync
        // directly -- a failed write here used to be silently swallowed (the caller's own Tracker.Track
        // line ran regardless, logging a false "success" even when nothing was actually persisted). This
        // is the fix for that: the intended outcome always gets logged distinctly from whether it was
        // actually written.
        {
            var result = await mailDataSvc.SaveMessageFinalizationAsync(finalize);
            if (result.IsFailure)
                Tracker.Track($"MsgNo {finalize.MsgNo}: failed to persist final status ({result.Message}) -- intended ResTpCode '{finalize.ResTpCode}' was NOT written.");
        }
        private async Task<(Result<ClassifyResult> Result, List<SenderHistory> History, ClaudeFieldSpec? LinkField)> ClassifyMessageAsync(MailAccount account, List<DocType> docTypes, List<MsgType> msgTypes, Dictionary<int, List<ClaudeFieldSpec>> fieldsByMsgType, string processDesc, Message message, int senderId)
        // Builds the classify content + runs it. History/isKnownSender/linkField only matter for accounts
        // with a sender-approval gate configured (GetSenderHistHndName non-empty, e.g. AP's vendor link) --
        // an account without one (e.g. LSM) always sends every attachment and skips the hold/approve path
        // entirely below. linkField is whichever field on the SUB entity is Searchable (VendId today) --
        // there is at most one across the current data, so "first msgtype with a fields-having entity" is
        // enough; an app that ever needs two distinct SUB entities would need this reconsidered.
        {
            var hasLinkGate = !string.IsNullOrEmpty(account.GetSenderHistHndName);
            var history = hasLinkGate ? await mailDataSvc.GetSenderHistoryAsync(account.GetSenderHistHndName, senderId, account.AppId) : [];
            var isKnownSender = history.Count > 0;

            var subType = msgTypes.FirstOrDefault(t => fieldsByMsgType.ContainsKey(t.MsgTpId));
            var linkField = subType is not null && fieldsByMsgType.TryGetValue(subType.MsgTpId, out var subFields)
                ? subFields.FirstOrDefault(f => f.FieldMode == "Searchable")
                : null;

            var content = new List<ClaudeContentPart>
            {
                new("text/plain", Text: $"From: {message.FromAddr}\nSubject: {message.Subject}\n\n{message.Body}"),
                new("text/plain", Text: $"Attachments: {(message.Attachments.Count > 0 ? string.Join(", ", message.Attachments.Select(a => a.FileName)) : "none")}")
            };

            if (linkField is not null)
            {
                var linked = history.Select(h => (h.VendId, h.VendName)).Distinct().ToList();
                var linkedText = linked.Count > 0 ? string.Join("; ", linked.Select(v => $"{linkField.FieldName} {v.VendId} ({v.VendName})")) : "This sender is not linked to anything yet.";
                content.Add(new ClaudeContentPart("text/plain", Text: $"{linkField.FieldName} already linked to this sender: {linkedText}"));
            }

            // Attachment images are only included once the sender has prior history -- a first-contact
            // sender hasn't been approved for anything, so nothing gets opened on their behalf until an
            // admin confirms it. Accounts with no link gate at all (isKnownSender never applies) always include.
            var includeExtraction = !hasLinkGate || isKnownSender;
            if (includeExtraction)
                content.AddRange(message.Attachments.Select(a => new ClaudeContentPart(a.MediaType, Data: a.Bytes)));

            var msgTypeOptions = msgTypes.Select(t => new ClaudeClassification { Id = t.MsgTpId, Code = t.MsgTpCode, Name = t.MsgTpDesc, Desc = t.ClassifyHint ?? t.MsgTpDesc }).ToList();
            var docTypeOptions = docTypes.Select(d => new ClaudeClassification { Id = d.DocTpId, Code = d.DocTpCode, Name = d.DocTpName, Desc = d.DocTpDesc }).ToList();

            var result = await classifier.ClassifyEmailAsync(content, processDesc, msgTypeOptions, docTypeOptions, fieldsByMsgType, includeExtraction);
            return (result, history, linkField);
        }
        private async Task CheckAndRequestMissingDocsAsync(IMailService mail, Message message, string fromAddr, int msgNo, List<int> pkgNos)
        // Runs right after a submission's items are saved -- one gap-check call covers every PkgNo the
        // submission created (ap.sprTblGetTasks accepts a list). One msg.TblInfoRequests row and one email
        // per PkgNo with outstanding gaps, not one combined email for the whole submission -- keeps the
        // bridge match unambiguous (rfibridge's SentMsgId lookup resolves to exactly one row) without needing
        // to work out which package a reply is about when a submission created more than one. AP-specific
        // (ap.LstInvcTypeDocTypes) -- only called for accounts with a sender-approval gate configured.
        {
            var tasks = await mailDataSvc.GetPkgTasksAsync(pkgNos);

            foreach (var group in tasks.GroupBy(t => t.PkgNo))
            {
                var gaps = group.Where(t => !t.IsComplete).ToList();
                if (gaps.Count == 0) continue;

                var openConv = await conv.OpenAsync("RFI");
                if (openConv.IsFailure)
                {
                    Tracker.Track($"PkgNo {group.Key}: failed to open conversation for RFI ({openConv.Message}).");
                    continue;
                }

                var body = GetInfoRequestBody(gaps);
                var sentMsgId = await mail.SendInfoRequestAsync(message.ProvMsgId, body);
                if (sentMsgId is null)
                {
                    Tracker.Track($"PkgNo {group.Key}: failed to send info request email.");
                    continue;
                }

                await conv.AppendItemAsync(openConv.Value.ConvNo, "assistant", body);

                var openRfi = await mailDataSvc.OpenInfoRequestAsync(new InfoRequest
                {
                    PkgNo = group.Key,
                    MsgNo = msgNo,
                    NotNo = null,
                    SentMsgId = sentMsgId,
                    SentTo = fromAddr,
                    ReqUId = null,
                    ConvNo = openConv.Value.ConvNo
                });

                if (openRfi.IsFailure)
                    Tracker.Track($"PkgNo {group.Key}: failed to open msg.TblInfoRequests ({openRfi.Message}).");
                else
                    Tracker.Track($"PkgNo {group.Key}: sent info request for {gaps.Count} outstanding item(s).");
            }
        }
        private async Task ProcessSelfSentMessageAsync(MailAccount account, Message message)
        // The polled mailbox occasionally emails itself -- e.g. an underlying system error/alert configured
        // against the same address EMF.Mail is watching for inbound submissions. Logged for visibility but
        // never classified, held, or replied to -- there's no sender to link and nothing to approve.
        {
            var msgId = message.InternetMessageId ?? message.ProvMsgId;

            var msgResult = await mailDataSvc.SaveMessageAsync(new MailMessage
            {
                AcctId = account.AcctId,
                MsgId = msgId,
                FromAddr = message.FromAddr,
                FromName = message.FromName,
                RcptDate = message.ReceivedDateTime,
                Subject = message.Subject,
                OrigMsgId = message.OrigMsgId
            });

            if (msgResult.IsFailure)
            {
                if (msgResult.Code != "2627")
                    Tracker.Track($"{message.FromAddr}: failed to log self-sent message ({msgResult.Message}).");
                return;
            }

            await FinalizeAsync(new MessageFinalization { MsgNo = msgResult.Value.MsgNo, ResTpCode = "NOOP", MsgResult = "Message from the account's own mailbox address, not a real submission." });
            Tracker.Track($"MsgNo {msgResult.Value.MsgNo}: message from the account's own address, logged and skipped.");
        }
        private async Task ProcessClassifiedMessageAsync(MailAccount account, IMailService mail, Message message, int msgNo, int senderId, List<MsgType> msgTypes, Dictionary<int, List<ClaudeFieldSpec>> fieldsByMsgType, ClassifyResult result, bool saveContext)
        // Acts on an already-classified message -- shared by the immediate path (classification just ran as
        // part of the hold decision, link field matched an already-approved value) and the post-approval
        // fan-out path (classification is reused from the original hold's saved MsgContext, not re-run).
        // saveContext is false on the fan-out path -- MsgContext was already written when the message was
        // first held, so the finalize call there only needs to touch MsgTpId/ResTpId/IsProcessed, not resave it.
        {
            var context = saveContext ? result : null;

            if (result.MsgTpCode is null)
            {
                await mail.MarkNeedsReviewAsync(message.ProvMsgId);
                await mail.ForwardAsync(message.ProvMsgId, account.AdmAcctEMail, "This message could not be classified and needs manual review.");
                await FinalizeAsync(new MessageFinalization { MsgNo = msgNo, MsgContext = context, ResTpCode = "REVIEW", MsgResult = "Could not determine message type." });
                Tracker.Track($"MsgNo {msgNo}: could not classify message, flagged for review.");
                return;
            }

            var msgType = msgTypes.FirstOrDefault(t => t.MsgTpCode == result.MsgTpCode);

            if (msgType is null)
            {
                await FinalizeAsync(new MessageFinalization { MsgNo = msgNo, MsgContext = context, MsgTpCode = result.MsgTpCode, ResTpCode = "FAILED", MsgResult = $"No message type configured for {result.MsgTpCode}." });
                Tracker.Track($"MsgNo {msgNo}: no message type configured for {result.MsgTpCode}.");
                return;
            }

            if (result.MsgTpCode == "INQ")
            {
                if (msgType.GetHndName is null)
                {
                    await FinalizeAsync(new MessageFinalization { MsgNo = msgNo, MsgContext = context, MsgTpCode = result.MsgTpCode, ResTpCode = "FAILED", MsgResult = $"No GetHndName configured for message type {result.MsgTpCode}." });
                    Tracker.Track($"MsgNo {msgNo}: no GetHndName configured for {result.MsgTpCode}.");
                    return;
                }

                var fields = fieldsByMsgType.GetValueOrDefault(msgType.MsgTpId) ?? [];
                var keyFields = fields.Where(f => f.IsKey && f.FieldMode != "Searchable").ToList();
                var pkgFields = result.Packages.FirstOrDefault()?.Fields;
                // Passed through as-is (possibly null) rather than coerced to "" -- an unspecified key should
                // reach the GetHndName proc as a real NULL, not a literal empty-string filter value.
                var keyValues = keyFields.Select(f => (object)(GetFieldString(pkgFields, f.FieldName)!)).ToArray();

                var matches = await mailDataSvc.GetRecordsAsync(msgType.GetHndName, [senderId, .. keyValues]);
                var reply = await classifier.ComposeReplyAsync(matches, msgType.ReplyPrompt ?? "Write a short, polite email reply summarizing the information given above, using only the information given.");

                if (reply.IsFailure)
                {
                    await FinalizeAsync(new MessageFinalization { MsgNo = msgNo, MsgContext = context, MsgTpCode = result.MsgTpCode, ResTpCode = "FAILED", MsgResult = $"Failed to compose inquiry reply: {reply.Message}" });
                    Tracker.Track($"MsgNo {msgNo}: failed to compose inquiry reply ({reply.Message}).");
                    return;
                }

                await mail.ReplyAsync(message.ProvMsgId, reply.Value);
                await mail.FlagProcessedAsync(message.ProvMsgId);
                await FinalizeAsync(new MessageFinalization { MsgNo = msgNo, MsgContext = context, MsgTpCode = result.MsgTpCode, ResTpCode = "OK", MsgResult = $"Inquiry reply sent ({matches.Count} match(es) found)." });
                Tracker.Track($"MsgNo {msgNo}: inquiry reply sent ({matches.Count} match(es) found).");
                return;
            }

            if (msgType.PutHndName is null)
            {
                await FinalizeAsync(new MessageFinalization { MsgNo = msgNo, MsgContext = context, MsgTpCode = result.MsgTpCode, ResTpCode = "FAILED", MsgResult = $"No PutHndName configured for message type {result.MsgTpCode}." });
                Tracker.Track($"MsgNo {msgNo}: no PutHndName configured for {result.MsgTpCode}.");
                return;
            }

            var items = new List<MessageItem>();

            // Consumed from this pool (not re-queried against message.Attachments directly) so two attachments
            // sharing the same FileName -- e.g. three PDFs all named "invoice.pdf" -- can't both resolve to the
            // same first match; each is claimed at most once.
            var remainingAttachments = new List<AttachmentContent>(message.Attachments);

            foreach (var pkg in result.Packages)
            {
                var anchor = pkg.Attachments.FirstOrDefault(a => a.IsPackage);
                int pkgNo;
                var reqNo = 0;

                if (anchor is not null)
                {
                    var attachment = remainingAttachments.FirstOrDefault(a => a.FileName == anchor.FileName);
                    if (attachment is null)
                    {
                        Tracker.Track($"MsgNo {msgNo}: attachment {anchor.FileName} not found or empty, skipping package.");
                        continue;
                    }
                    remainingAttachments.Remove(attachment);

                    var docResult = anchor.DocTpId is not null && anchor.ExtractedFields is not null
                        ? await filer.SaveExtractedDocumentAsync(account.AppId, account.OwnerUId, anchor.DocTpId.Value, anchor.ExtractedFields, attachment.Bytes, anchor.FileName)
                        : await filer.ProcessDocumentAsync(account.AppId, account.OwnerUId, attachment.Bytes, attachment.MediaType, anchor.FileName);

                    if (docResult.IsFailure)
                    {
                        Tracker.Track($"MsgNo {msgNo}: {docResult.Message}");
                        continue;
                    }

                    pkgNo = docResult.Value.PkgNo;
                    reqNo = docResult.Value.ReqNo;
                    items.Add(new MessageItem { MsgNo = msgNo, PkgNo = pkgNo, DocNo = docResult.Value.DocNo });
                    Tracker.Track($"MsgNo {msgNo}: created ReqNo {reqNo} (PkgNo {pkgNo}) from {anchor.FileName}.");
                }
                else
                {
                    var putResult = await filerDataSvc.SaveRequestAsync(msgType.PutHndName, pkg.Fields);
                    if (putResult.IsFailure)
                    {
                        Tracker.Track($"MsgNo {msgNo}: {putResult.Message}");
                        continue;
                    }

                    pkgNo = putResult.Value.PkgNo;
                    reqNo = putResult.Value.ReqNo;
                    items.Add(new MessageItem { MsgNo = msgNo, PkgNo = pkgNo, DocNo = putResult.Value.DocNo });
                    Tracker.Track($"MsgNo {msgNo}: saved {result.MsgTpCode} record (PkgNo {pkgNo}).");
                }

                foreach (var supporting in pkg.Attachments.Where(a => !a.IsPackage))
                {
                    var suppAttachment = remainingAttachments.FirstOrDefault(a => a.FileName == supporting.FileName);
                    if (suppAttachment is null)
                    {
                        Tracker.Track($"MsgNo {msgNo}: supporting attachment {supporting.FileName} not found or empty, skipping.");
                        continue;
                    }
                    remainingAttachments.Remove(suppAttachment);

                    var attachResult = supporting.DocTpId is not null
                        ? await filer.AttachDocumentAsync(account.AppId, pkgNo, supporting.DocTpId.Value, suppAttachment.Bytes, supporting.FileName)
                        : await filer.AttachDocumentAsync(pkgNo, suppAttachment.Bytes, supporting.FileName);

                    if (attachResult.IsFailure)
                    {
                        Tracker.Track($"MsgNo {msgNo}: {attachResult.Message}");
                        continue;
                    }

                    items.Add(new MessageItem { MsgNo = msgNo, PkgNo = pkgNo, DocNo = attachResult.Value });
                }
            }

            if (items.Count > 0)
                await mailDataSvc.SaveMessageItemsAsync(items);

            var pkgNosCreated = items.Select(i => i.PkgNo).Distinct().ToList();

            await mail.FlagProcessedAsync(message.ProvMsgId);
            var replyBody = msgType.ReplyMessage?.Replace("{numPkgs}", pkgNosCreated.Count.ToString());

            if (pkgNosCreated.Count > 0)
            {
                if (!string.IsNullOrEmpty(replyBody))
                    await mail.ReplyAsync(message.ProvMsgId, replyBody);

                await FinalizeAsync(new MessageFinalization { MsgNo = msgNo, MsgContext = context, MsgTpCode = result.MsgTpCode, ResTpCode = "OK" });

                if (!string.IsNullOrEmpty(account.GetSenderHistHndName))
                    await CheckAndRequestMissingDocsAsync(mail, message, message.FromAddr, msgNo, pkgNosCreated);
            }
            else
            {
                await FinalizeAsync(new MessageFinalization { MsgNo = msgNo, MsgContext = context, MsgTpCode = result.MsgTpCode, ResTpCode = "NOOP", MsgResult = "No packages created from this message." });
                Tracker.Track($"MsgNo {msgNo}: no packages created from this message.");
            }
        }
        private async Task ProcessInboundMessageAsync(MailAccount account, IMailService mail, List<DocType> docTypes, List<MsgType> msgTypes, Dictionary<int, List<ClaudeFieldSpec>> fieldsByMsgType, string processDesc, Message message)
        // Handles one inbound (non-admin) message: log it, classify it, then either hold for approval
        // or process it immediately depending on the link field (e.g. VendId) -- or, for an account with
        // no sender-approval gate configured at all, straight to processing every time.

        {
            var msgId = message.InternetMessageId ?? message.ProvMsgId;

            var msgResult = await mailDataSvc.SaveMessageAsync(new MailMessage
            {
                AcctId = account.AcctId,
                MsgId = msgId,
                FromAddr = message.FromAddr,
                FromName = message.FromName,
                RcptDate = message.ReceivedDateTime,
                Subject = message.Subject,
                OrigMsgId = message.OrigMsgId
            });

            if (msgResult.IsFailure)
            {
                if (msgResult.Code == "2627")
                    Tracker.Track($"{message.FromAddr}: message already logged, skipping.");
                else
                    Tracker.Track($"{message.FromAddr}: failed to log message ({msgResult.Message}).");
                return;
            }

            var msgNo = msgResult.Value.MsgNo;
            var senderId = msgResult.Value.SenderId;

            var (classifyResult, history, linkField) = await ClassifyMessageAsync(account, docTypes, msgTypes, fieldsByMsgType, processDesc, message, senderId);

            if (classifyResult.IsFailure)
            {
                await mail.MarkNeedsReviewAsync(message.ProvMsgId);
                await mail.ForwardAsync(message.ProvMsgId, account.AdmAcctEMail, "This message could not be classified and needs manual review.");
                await FinalizeAsync(new MessageFinalization { MsgNo = msgNo, ResTpCode = "REVIEW", MsgResult = $"Claude classification failed: {classifyResult.Message}" });
                Tracker.Track($"MsgNo {msgNo}: Claude classification failed ({classifyResult.Message}).");
                return;
            }

            var result = classifyResult.Value;
            var pkgFields = result.Packages.FirstOrDefault()?.Fields;
            var linkValue = linkField is not null ? GetFieldInt(pkgFields, linkField.FieldName) : null;
            var isLinked = linkField is null || (linkValue is not null && history.Any(h => h.VendId == linkValue));

            if (!isLinked)
            {
                var isKnownSender = history.Count > 0;

                // A sender's second (or Nth) message naming the same not-yet-approved link value gets held
                // silently -- only the first pending message for that (sender, value) pair triggers the
                // forward to admin. linkValue is passed through as-is (possibly null) -- the SQL side
                // matches a held row with an unresolved value regardless, so a first-contact sender's own
                // duplicate holds still dedup.
                var pending = await mailDataSvc.GetHeldMessagesAsync(senderId, linkValue, msgNo);

                string? fwdMsgId = null;

                // Computed once, reused for both the admin email and the DB write below -- avoids running the
                // same gap check twice and keeps the two from ever disagreeing with each other.
                var missingInfo = GetMissingInfo(result, linkField!, isKnownSender);

                if (pending.Count == 0)
                    fwdMsgId = await mail.SendApprovalRequestAsync(message.ProvMsgId, account.AdmAcctEMail, GetApprovalComment(result, linkField!, isKnownSender, missingInfo));

                var resTpCode = missingInfo.Count > 0 ? "PARTIAL" : "HELD";
                var msgResultText = missingInfo.Count > 0 ? string.Join("; ", missingInfo) : null;

                var logMsg = pending.Count == 0
                    ? $"MsgNo {msgNo} ({message.FromAddr}): sender not linked to identified {linkField!.FieldName}, held pending admin approval."
                    : $"MsgNo {msgNo} ({message.FromAddr}): already has a pending request for this {linkField!.FieldName}, held silently.";

                await FinalizeAsync(new MessageFinalization { MsgNo = msgNo, MsgContext = result, MsgTpCode = result.MsgTpCode, IsHeld = true, FwdMsgId = fwdMsgId, ResTpCode = resTpCode, MsgResult = msgResultText });
                Tracker.Track(logMsg);
                return;
            }

            await ProcessClassifiedMessageAsync(account, mail, message, msgNo, senderId, msgTypes, fieldsByMsgType, result, saveContext: true);
        }
        private async Task ProcessInfoRequestReplyAsync(MailAccount account, IMailService mail, Message message, RfiBridgeResult bridge, List<DocType> docTypes, string processDesc)
        // Handles one reply that bridged back to an open msg.TblInfoRequests row (see the rfibridge check
        // in ProcessAccountAsync). Attachments are classified only against that package's still-outstanding
        // gaps (ap.sprTblGetTasks), not the full doc-type list -- a match attaches with the real DocTpCode
        // (Filer.AttachDocumentAsync's docTypeId overload) instead of falling back to "Misc". No Claude call
        // interprets the reply text itself -- attachments-only scope doesn't need it (see ProjectContext);
        // the conversation log here is audit trail, not something replayed back into a live Claude call.
        //
        // Ask-once design: this reply is the ONLY round -- no resend/re-ask to the vendor regardless of
        // outcome (see ProjectContext). Whether every gap is satisfied or some remain, the InfoRequest is
        // closed here and the admin is notified; no further automated contact with the sender on this
        // request. Vendor gets no reply at all in either branch for now -- deliberate, a later admin-command
        // pass may add one back. Unaffected by this redesign -- doesn't touch ClassifyResult/Packages at all.

        {
            var msgId = message.InternetMessageId ?? message.ProvMsgId;

            var msgResult = await mailDataSvc.SaveMessageAsync(new MailMessage
            {
                AcctId = account.AcctId,
                MsgId = msgId,
                FromAddr = message.FromAddr,
                FromName = message.FromName,
                RcptDate = message.ReceivedDateTime,
                Subject = message.Subject,
                OrigMsgId = message.OrigMsgId
            });

            if (msgResult.IsFailure)
            {
                if (msgResult.Code == "2627")
                    Tracker.Track($"{message.FromAddr}: RFI reply already logged, skipping.");
                else
                    Tracker.Track($"{message.FromAddr}: failed to log RFI reply ({msgResult.Message}).");
                return;
            }

            var msgNo = msgResult.Value.MsgNo;

            var attachments = message.Attachments;

            var replyText = GetPlainText(message.UniqueBody);
            await conv.AppendItemAsync(bridge.ConvNo, "user", $"{replyText}\nAttachments: {(attachments.Count > 0 ? string.Join(", ", attachments.Select(a => a.FileName)) : "none")}");

            var outstanding = (await mailDataSvc.GetPkgTasksAsync([bridge.PkgNo])).Where(t => !t.IsComplete).ToList();

            // Items collected across the whole reply and saved once below -- was one SaveMessageItemsAsync
            // call per attachment, now a single round trip regardless of how many attachments matched.
            var items = new List<MessageItem>();

            if (outstanding.Count > 0 && attachments.Count > 0)
            {
                var options = outstanding
                    .Select(t => docTypes.FirstOrDefault(d => d.DocTpId == t.DocTypeId))
                    .Where(d => d is not null)
                    .Select(d => new ClaudeClassification { Id = d!.DocTpId, Code = d.DocTpCode, Name = d.DocTpName, Desc = d.DocTpDesc })
                    .ToList();

                foreach (var attachment in attachments)
                {
                    var classifyResult = await classifier.ClassifyAsync(attachment.Bytes, attachment.MediaType, processDesc, options);

                    if (classifyResult.IsFailure || !options.Any(o => o.Id == classifyResult.Value.Id))
                    {
                        Tracker.Track($"IReqNo {bridge.IReqNo}: attachment {attachment.FileName} did not match an outstanding requirement, skipping.");
                        continue;
                    }

                    var attachResult = await filer.AttachDocumentAsync(account.AppId, bridge.PkgNo, classifyResult.Value.Id, attachment.Bytes, attachment.FileName);
                    if (attachResult.IsFailure)
                    {
                        Tracker.Track($"IReqNo {bridge.IReqNo}: failed to attach {attachment.FileName} ({attachResult.Message}).");
                        continue;
                    }

                    items.Add(new MessageItem { MsgNo = msgNo, PkgNo = bridge.PkgNo, DocNo = attachResult.Value });
                    Tracker.Track($"IReqNo {bridge.IReqNo}: attached {attachment.FileName} as DocTypeId {classifyResult.Value.Id}.");
                }

                if (items.Count > 0)
                    await mailDataSvc.SaveMessageItemsAsync(items);
            }

            var remaining = (await mailDataSvc.GetPkgTasksAsync([bridge.PkgNo])).Where(t => !t.IsComplete).ToList();

            // Closed unconditionally here -- no resend branch anymore, see method comment above.
            await mailDataSvc.CloseInfoRequestAsync(bridge.IReqNo);

            // IReqNo now rides in the finalize DTO itself -- the separate LinkReplyAsync/linkreply call is gone.
            if (remaining.Count == 0)
            {
                await conv.AppendItemAsync(bridge.ConvNo, "assistant", "All required items received.");
                await mail.ForwardAsync(message.ProvMsgId, account.AdmAcctEMail, $"RFI reply processed for PkgNo {bridge.PkgNo}: all required items received.");
                await FinalizeAsync(new MessageFinalization { MsgNo = msgNo, MsgTpCode = "SUB", IReqNo = bridge.IReqNo, ResTpCode = "OK", MsgResult = "RFI resolved, all items received." });
                Tracker.Track($"IReqNo {bridge.IReqNo}: all items received, closed.");
            }
            else
            {
                var stillMissing = string.Join("; ", remaining.Select(t => t.Task));
                await conv.AppendItemAsync(bridge.ConvNo, "assistant", $"Still missing after reply: {stillMissing}.");
                await mail.ForwardAsync(message.ProvMsgId, account.AdmAcctEMail, $"RFI reply processed for PkgNo {bridge.PkgNo}, but {remaining.Count} item(s) still outstanding: {stillMissing}. This request will not be re-asked automatically -- please follow up.");
                await FinalizeAsync(new MessageFinalization { MsgNo = msgNo, MsgTpCode = "SUB", IReqNo = bridge.IReqNo, ResTpCode = "REVIEW", MsgResult = $"{remaining.Count} item(s) still outstanding after RFI reply; closed without re-asking, admin notified." });
                Tracker.Track($"IReqNo {bridge.IReqNo}: {remaining.Count} item(s) still outstanding after reply, closed (no re-ask), admin notified.");
            }
        }
        private async Task ReprocessHeldMessageAsync(MailAccount account, IMailService mail, HeldMessage heldMsg, int senderId, List<MsgType> msgTypes, Dictionary<int, List<ClaudeFieldSpec>> fieldsByMsgType)
        // Reprocesses one previously-held message after its link value is approved -- reuses the
        // classification saved in MsgContext at hold time instead of re-running Claude.

        {
            var orig = await mail.GetMessageByIdAsync(heldMsg.MsgId);
            if (orig is null)
            {
                await FinalizeAsync(new MessageFinalization { MsgNo = heldMsg.MsgNo, ResTpCode = "FAILED", MsgResult = "Approved but original message could not be refetched from Inbox." });
                Tracker.Track($"MsgNo {heldMsg.MsgNo}: approved but original message could not be refetched from Inbox.");
                return;
            }

            ClassifyResult? result = null;
            try
            {
                if (heldMsg.MsgContext is not null)
                    result = JsonSerializer.Deserialize<ClassifyResult>(heldMsg.MsgContext, _jsonOpts);
            }
            catch (JsonException)
            {
                result = null;
            }

            if (result is null)
            {
                await mail.MarkNeedsReviewAsync(orig.ProvMsgId);
                await mail.ForwardAsync(orig.ProvMsgId, account.AdmAcctEMail, "This message's saved classification could not be read and needs manual review.");
                await FinalizeAsync(new MessageFinalization { MsgNo = heldMsg.MsgNo, ResTpCode = "REVIEW", MsgResult = "Saved MsgContext missing or unreadable on reprocess." });
                Tracker.Track($"MsgNo {heldMsg.MsgNo}: saved MsgContext missing or unreadable on reprocess.");
                return;
            }

            await ProcessClassifiedMessageAsync(account, mail, orig, heldMsg.MsgNo, senderId, msgTypes, fieldsByMsgType, result, saveContext: false);
        }
        private async Task ProcessAdminReplyAsync(MailAccount account, IMailService mail, Message message, List<MsgType> msgTypes, Dictionary<int, List<ClaudeFieldSpec>> fieldsByMsgType)
        // Handles one admin reply: match it to a held message, interpret APPROVE/REJECT, act on it, and
        // fan out to every other message the sender has pending. Every admin message gets its own logged
        // row and a reply -- no path here leaves the admin without a response. Unaffected by this redesign
        // apart from threading msgTypes/fieldsByMsgType through to the reprocess call below.

        {
            var msgId = message.InternetMessageId ?? message.ProvMsgId;

            var msgResult = await mailDataSvc.SaveMessageAsync(new MailMessage
            {
                AcctId = account.AcctId,
                MsgId = msgId,
                FromAddr = message.FromAddr,
                FromName = message.FromName,
                RcptDate = message.ReceivedDateTime,
                Subject = message.Subject,
                OrigMsgId = message.OrigMsgId
            });

            if (msgResult.IsFailure)
            {
                if (msgResult.Code == "2627")
                    Tracker.Track($"{message.FromAddr}: admin message already logged, skipping.");
                else
                    Tracker.Track($"{message.FromAddr}: failed to log admin message ({msgResult.Message}).");
                return;
            }

            var adminMsgNo = msgResult.Value.MsgNo;

            var held = (await mailDataSvc.GetHeldBridgeAsync(message.BridgeMsgIds)).FirstOrDefault();

            if (held is null)
            {
                await mail.ReplyAsync(message.ProvMsgId, "This reply doesn't correspond to a message currently on hold -- there's nothing to act on.");
                await FinalizeAsync(new MessageFinalization { MsgNo = adminMsgNo, MsgTpCode = "CMD", ResTpCode = "REVIEW", MsgResult = "No matching hold found." });
                Tracker.Track($"Admin message from {message.FromAddr}: no matching hold.");
                return;
            }

            Tracker.Track($"Admin reply from {message.FromAddr} matched held MsgNo {held.MsgNo}.");

            var cmdResult = await cmdSvc.InterpretApprovalReplyAsync(message.Subject, message.UniqueBody, held.CandVendName);

            if (cmdResult.IsFailure)
            {
                await mail.ReplyAsync(message.ProvMsgId, "Something went wrong interpreting that reply. Please try again.");
                await FinalizeAsync(new MessageFinalization { MsgNo = adminMsgNo, MsgTpCode = "CMD", ResTpCode = "FAILED", MsgResult = $"Claude command interpretation failed: {cmdResult.Message}" });
                Tracker.Track($"MsgNo {held.MsgNo}: Claude command interpretation failed ({cmdResult.Message}).");
                return;
            }

            // Explicit both ways -- anything that isn't exactly APPROVE or REJECT (including null) is
            // treated as "didn't understand," never silently folded into a REJECT.
            if (cmdResult.Value.CmdCode != "APPROVE" && cmdResult.Value.CmdCode != "REJECT")
            {
                await mail.ReplyAsync(message.ProvMsgId, "Sorry, I couldn't understand that reply. Please reply APPROVE, REJECT, or the correct vendor name.");
                await FinalizeAsync(new MessageFinalization { MsgNo = adminMsgNo, MsgTpCode = "CMD", ResTpCode = "REVIEW", MsgResult = "Admin reply did not resolve to APPROVE/REJECT." });
                Tracker.Track($"MsgNo {held.MsgNo}: admin reply did not resolve to APPROVE/REJECT, left on hold.");
                return;
            }

            // Reuse the candidate VendId directly on a bare confirmation (no correction given) -- only
            // re-resolve via lookup when the admin named something different, or there was no candidate at all.
            int? vendId = held.CandVendId;
            if (cmdResult.Value.VendorName is not null && !string.Equals(cmdResult.Value.VendorName, held.CandVendName, StringComparison.OrdinalIgnoreCase))
            {
                var matches = await mailDataSvc.GetLookupAsync(cmdResult.Value.VendorName);
                if (matches.Count != 1)
                {
                    await mail.ReplyAsync(message.ProvMsgId, $"Vendor \"{cmdResult.Value.VendorName}\" not found. Please reply with the exact vendor name.");
                    await FinalizeAsync(new MessageFinalization { MsgNo = adminMsgNo, MsgTpCode = "CMD", ResTpCode = "REVIEW", MsgResult = $"Vendor \"{cmdResult.Value.VendorName}\" not found (or ambiguous)." });
                    Tracker.Track($"MsgNo {held.MsgNo}: vendor \"{cmdResult.Value.VendorName}\" not found (or ambiguous), left on hold.");
                    return;
                }
                vendId = matches[0].VendId;
            }

            if (vendId is null)
            {
                await mail.ReplyAsync(message.ProvMsgId, "Please reply with the vendor name to link this sender to.");
                await FinalizeAsync(new MessageFinalization { MsgNo = adminMsgNo, MsgTpCode = "CMD", ResTpCode = "REVIEW", MsgResult = "No vendor could be resolved." });
                Tracker.Track($"MsgNo {held.MsgNo}: no vendor could be resolved, left on hold.");
                return;
            }

            var isApproved = cmdResult.Value.CmdCode == "APPROVE";

            // Fetched before ResolveCommandAsync -- that call clears IsHeld on every match, so the pending
            // set has to be captured first or there'd be nothing left to reprocess/report on.
            var pending = await mailDataSvc.GetHeldMessagesAsync(held.SenderId, vendId.Value, held.MsgNo);

            if (isApproved)
            {
                var linkResult = await mailDataSvc.SaveSenderLinkAsync(held.SenderId, account.AppId, vendId.Value);
                if (linkResult.IsFailure)
                {
                    await mail.ReplyAsync(message.ProvMsgId, "Something went wrong linking this vendor. Please try again or contact support.");
                    await FinalizeAsync(new MessageFinalization { MsgNo = adminMsgNo, MsgTpCode = "CMD", ResTpCode = "FAILED", MsgResult = $"Failed to link vendor: {linkResult.Message}" });
                    Tracker.Track($"MsgNo {held.MsgNo}: failed to link VendId {vendId} to SenderId {held.SenderId} ({linkResult.Message}).");
                    return;
                }
            }

            await mailDataSvc.SaveMessageResolutionAsync(new MessageResolution
            {
                SenderId = held.SenderId,
                VendId = vendId.Value,
                AnchorMsgNo = held.MsgNo,
                IsApproved = isApproved,
                CmdCode = cmdResult.Value.CmdCode!,
                AdminMsgNo = adminMsgNo,
                Reference = $"SenderId {held.SenderId}, VendId {vendId.Value}",
                ResultCode = 0,
                ResultMsg = isApproved ? "Approved" : "Rejected"
            });

            Tracker.Track($"{message.FromAddr}: {(isApproved ? "approved" : "rejected")} VendId {vendId} -- {pending.Count} message(s) affected.");

            if (isApproved)
                foreach (var heldMsg in pending)
                    await ReprocessHeldMessageAsync(account, mail, heldMsg, held.SenderId, msgTypes, fieldsByMsgType);

            await mail.ReplyAsync(message.ProvMsgId, isApproved
                ? $"Approved. {pending.Count} message(s) for this vendor were processed."
                : $"Rejected. {pending.Count} message(s) were declined.");

            await FinalizeAsync(new MessageFinalization { MsgNo = adminMsgNo, MsgTpCode = "CMD", ResTpCode = "OK" });
        }
        #endregion
    }
}
