using Cheing;
using Cheing.Net.Ai;
using EMF.DMS.Client;
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
    // Header Section ==================================================================================
    // Author: Bernie Cheing
    // 
    // MessageProcessor -- single processor for every app (TriageService/MsgTypeProcessor retired).
    // No per-AppId branching: approval gating comes from MsgType.IsApprRequired/PartyTpId (per SUB
    // entity), and document-vs-body packages are decided per PackageInstance by whether it has an
    // IsPackage (anchor) attachment. mail is supplied by Program.cs per account ProvCode; this class
    // never knows which provider it talks to.
    //
    // PARTIES
    // The party a sender submits for (vendor, customer) is generic, configured in ou.LstPartyTypes:
    // PartyTpName (admin-facing label), PartyTpKeyField/PartyTpNameField (field names in extracted
    // data, e.g. VendId/VendName), LookupHndName (name -> id resolution; null = no lookup available).
    // A sender is authorized per (SenderId, PartyTpId, PartyId) in msg.LstSenderPermissions.
    //
    // ProcessAccountAsync
    // Changed messages are fetched first; app config (ApplicationExt: process, msgtypes/doctypes/fields)
    // and party types are fetched once per run, only when there is mail to process, and those
    // independent fetches run via Task.WhenAll. Routing order per message:
    // self-sent -> admin reply -> RFI reply bridge -> normal inbound. An RFI reply is checked before
    // normal classification so it is never re-triaged as a fresh SUB/INQ.
    //
    // ProcessInboundMessageAsync / ClassifyMessageAsync
    // Log, classify, then hold or process. The hold gate uses the CLASSIFIED message type's own
    // IsApprRequired (SUB and INQ today); an unclassifiable message (MsgTpCode null) is never held --
    // it goes to REVIEW. Types without approval (e.g. LSM Vehicle Shipment) always send every
    // attachment and skip the hold/approve path. AssignAnchors runs right after classification, so a
    // held context and an immediate save agree.
    // FIRST-CONTACT RULE: an unknown sender's attachments are never opened (spam, oversized or
    // compromised files) -- classification uses the email text only; files are opened only after an
    // admin approves the sender. A known sender's attachments are sent on the first pass.
    // SenderHistory (reinforcement hint) is separate from authorization and not yet wired.
    //
    // GetApprovalComment
    // Admin-facing text for a hold/approval-request email: echoes what was extracted (approval is not
    // a blind click), names the candidate party using PartyTpName, and lists anything missing so one
    // reply can both decide and supply the gap. missingInfo is passed in (caller already built it).
    //
    // GetMissingInfo
    // Terse gaps that drive PARTIAL vs HELD; no MsgNo/sender/reference numbers (already on the row).
    // The party key field only counts as a gap for a known sender -- for first contact, an unresolved
    // party is the deliberate result of not opening the attachment yet.
    //
    // ProcessAdminReplyAsync -- APPROVE/REJECT on an open msg.TblApprRequests row
    // Every admin message gets its own logged row and a reply; no path leaves the admin unanswered.
    // 1. Bridge the reply to its request via References/In-Reply-To (/msg/mail/apprrequest). Only an
    //    open request (IsApproved null) matches.
    // 2. CommandService interprets the reply (APPROVE/REJECT + optional PartyName). A named party that
    //    differs from the candidate (or any name when no candidate id exists) is resolved through
    //    LookupHndName; exactly one match is required, otherwise the admin is asked for the exact name.
    // 3. APPROVE with no resolvable party -> admin asked to name it; request stays open.
    // 4. APPROVE -- DEFERRED WRITE: before anything is persisted, every held message is prepared
    //    (PrepareHeldMessageAsync). If any package's party contradicts the approved one, NOTHING is
    //    written except the extractions: the admin gets the discrepancy text and the request stays
    //    open, so their next reply on the same thread bridges back to it.
    // 5. Only when all held messages agree is the decision written (/filer/msg/resolvecmd: one
    //    transaction -- request resolved, sender permission inserted if not already present, command
    //    logged). A failed write is reported to the admin and nothing is processed.
    // 6. REJECT: written the same way (PartyId kept as the original candidate); every held message is
    //    finalized REJECTED and the admin gets a confirmation reply (REJECT is an admin command).
    //
    // PrepareHeldMessageAsync -- post-approval only
    // Refetches the original, reads the saved MsgContext, and opens any anchor not yet extracted
    // (Filer.ClassifyAndExtractAsync -- extract only, nothing saved).
    // Anchor choice is data-driven (AssignAnchors): an attachment whose DocTpId belongs to the classified
    // entity's RecTpId becomes the package anchor when Claude chose none (promote only, never demote).
    // A package still without an anchor has its unlabeled files opened; the first that classifies as
    // the entity's own record type is promoted and its extraction kept.
    // MsgContext is written back once, at the end (/filer/msg/context; message stays HELD), after any
    // discard below, so a later reply never reopens a file whose result was kept.
    // Each package's party is then compared to the approved one -- source is the anchor's
    // ExtractedFields when present, otherwise the body Fields:
    //
    // ReprocessHeldMessageAsync -- after the approval is written
    // Refetch failed -> FAILED. Unreadable MsgContext -> REVIEW + forwarded to admin. An anchor that
    // could not be extracted (unverified party) -> REVIEW + forwarded, never processed. Otherwise an
    // empty party field is filled with the approved party (never overwritten) and the saved
    // classification is processed via ProcessClassifiedMessageAsync (no second Claude call; Filer
    // saves pre-extracted anchors via SaveExtractedDocumentAsync).
    //
    // ProcessClassifiedMessageAsync
    // Shared by the immediate path and the post-approval path. saveContext is false post-approval
    // (MsgContext already saved). Attachments are claimed from a pool, not re-matched by FileName, so
    // several files with the same name each resolve at most once. A null lookup key is passed to the
    // GetHndName proc as a real NULL, not "".
    //
    // CheckAndRequestMissingDocsAsync -- request for missing docs
    // Runs after a submission's items are saved, only when the message type's IsTaskCheckRequired is set
    // and the app has an OnTasksGet handler (dms.LstApps). One call covers all PkgNos; one
    // msg.TblInfoRequests row and one email per PkgNo with missing docs (keeps the rfibridge match
    // unambiguous).
    //
    // ProcessInfoReplyAsync -- reply to an open RFI
    // Attachments are classified only against that package's outstanding gaps and attached with the
    // real DocTpCode (not "Misc"). Reply text is not interpreted; the conversation log is audit only.
    // Ask-once: this reply is the only round -- the RFI is closed whether or not all gaps are met, the
    // admin is notified, and the sender gets no reply. Items are saved in one call for the whole reply.
    // IReqNo rides in the finalize DTO (no separate link call).
    //
    // ProcessSelfSentMessageAsync
    // The mailbox occasionally emails itself (e.g. a system alert): logged only, never classified,
    // held, or replied to.
    //
    // FinalizeAsync
    // Single terminal write point for every message. The intended outcome is always logged separately
    // from whether the write succeeded -- a failed write is never reported as success.
    //
    // PENDING: when the total size of a message's attachments exceeds a threshold (not yet set), triage
    // it as NeedsReview before any file is sent to Claude.
    // ============================================================================================
    public partial class MessageProcessor(DataService mailDataSvc, FilerDataService filerDataSvc, CommandService cmdSvc, ClaudeClassifier classifier, Filer filer, ConversationService conv, PkgService dms)
    {
        private const int ReminderThresholdDays = 7;
        [GeneratedRegex(@"\s+")]
        private static partial Regex WhitespaceRegex();
        private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };
        private sealed record HeldMessage(ApprRequestItem Item, InternetMessage? Orig, ClassifyResult? Result, bool IsVerified, List<string> Mismatches);

        #region Public methods. Main entry point from console app.
        public async Task ProcessAccountAsync(Account account, IMailService mail)
        {
            var (messages, deltaLink) = await mail.GetChangedMessagesAsync(account.LastMsgLink);
            Tracker.Track($"Account {account.AcctName}: {messages.Count} message(s) fetched.");

            if (messages.Count > 0)
            {
                var appTask = filerDataSvc.GetApplicationExtAsync(account.AppId);
                var partyTypesTask = dms.GetPartyTypesAsync();

                await Task.WhenAll(appTask, partyTypesTask);

                var app = appTask.Result;
                var partyTypes = partyTypesTask.Result;

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
                            await ProcessAdminReplyAsync(account, mail, app, partyTypes, message);
                        }
                        else
                        {
                            // Checked before normal classification -- a reply to an open RFI takes this path
                            // instead of being triaged again as a fresh Submission/Inquiry.
                            var rfiBridge = await mailDataSvc.GetRfiBridgeAsync(message.BridgeMsgIds);

                            if (rfiBridge is not null && app.OnTasksGet is string tasksHndName)
                                await ProcessInfoRequestReplyAsync(account, mail, message, rfiBridge, app, tasksHndName);
                            else
                                await ProcessInboundMessageAsync(account, mail, app, partyTypes, message);
                        }
                    }
                    catch (MessageLogException ex)
                    {
                        Tracker.Notify($"Account {account.AcctName}: {ex.Message} Run stopped, bookmark not saved.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Tracker.Track($"{message.FromAddr}: unhandled exception processing message ({ex.Message}).");
                    }
                }
            }

            var pollResult = await mailDataSvc.SaveMessageBookmarkAsync(new AccountBookmark { AcctId = account.AcctId, LastMsgLink = deltaLink });
            if (pollResult.IsFailure)
                Tracker.Track($"Account {account.AcctName}: failed to update LastPollDT/LastMsgLink ({pollResult.Message}).");
        }
        #endregion

        #region Message log
        private sealed class MessageLogException(string message) : Exception(message);

        // ai: A message that cannot be logged must not be processed; any failure other than 2627 (already logged) stops the account run.
        private async Task<Result<MessageResult>> LogMessageAsync(Account account, InternetMessage message)
        {
            var msgResult = await mailDataSvc.SaveMessageAsync(new Message
            {
                AcctId = account.AcctId,
                MsgId = message.InternetMessageId ?? message.ProvMsgId,
                FromAddr = message.FromAddr,
                FromName = message.FromName,
                RcptDT = message.ReceivedDateTime,
                Subject = message.Subject,
                OrigMsgId = message.OrigMsgId,
                NumAttachments = (byte)message.Attachments.Count
            });

            if (msgResult.IsFailure && msgResult.Code != "2627")
                throw new MessageLogException($"{message.FromAddr}: failed to log message ({msgResult.Message}).");

            return msgResult;
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
        private static string GetInfoRequestBody(List<PkgTask> gaps) => "We need some additional documentation before we can process your request: " + string.Join("; ", gaps.Select(g => g.Task)) + ".";
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
        private static string GetApprovalComment(ClassifyResult result, PartyType partyType, bool isKnownSender, List<string> missingInfo, int numPendingItems = 0, DateTime? reqDT = null)
        {
            var fields = result.Packages.FirstOrDefault()?.Fields;
            var partyId = GetFieldInt(fields, partyType.PartyTpKeyField);
            var name = GetFieldString(fields, partyType.PartyTpNameField);

            var lines = new List<string>();

            if (numPendingItems > 0 && reqDT is not null)
                lines.Add($"Reminder: this sender has had {numPendingItems} message(s) pending approval since {reqDT:d}.");

            var label = partyType.PartyTpName.ToLower();
            string ask;
            if (partyId is not null && name is not null)
                ask = $"This sender is not yet linked to any {label}. It looks like it may be from {name} ({partyType.PartyTpKeyField} {partyId}). Reply APPROVE to link it, REJECT to decline, or give the correct name.";
            else if (name is not null)
                ask = $"This sender is not yet linked to any {label}. It looks like it may be from \"{name}\" -- reply APPROVE to link it, REJECT to decline, or give the correct name.";
            else
                ask = $"This sender is not yet linked to any {label} and none could be determined from the message. Reply with the correct {label} name to link it to, or REJECT to decline.";
            lines.Add(ask);

            if (result.Packages.Any(p => p.Attachments.Any(a => a.IsPackage)) && !isKnownSender)
                lines.Add("Due to security concerns, I haven't opened the attachments yet -- I need to confirm this sender is allowed to submit first. They'll be processed once approved.");

            lines.AddRange(missingInfo.Select(g => char.ToUpper(g[0]) + g[1..] + "."));

            return string.Join(" ", lines);
        }
        private static List<string> GetMissingInfo(ClassifyResult result, PartyType partyType, bool isKnownSender)
        {
            var gaps = new List<string>();

            if (result.MsgTpCode is null)
                gaps.Add("message type could not be determined");
            else if (result.Packages.Count == 0)
                gaps.Add("no packages could be identified for processing");

            var partyValue = GetFieldValue(result.Packages.FirstOrDefault()?.Fields, partyType.PartyTpKeyField);
            if (isKnownSender && partyValue is null)
                gaps.Add($"{partyType.PartyTpName.ToLower()} could not be confirmed even after reviewing the attachment");

            return gaps;
        }    
        private static void FillPartyFields(Dictionary<string, object> fields, Party party)
        {
            if (GetFieldInt(fields, party.Type.PartyTpKeyField) is not null) return;
            fields[party.Type.PartyTpKeyField] = party.Id;
            if (party.Name is not null) fields[party.Type.PartyTpNameField] = party.Name;
        }
        private static void AssignAnchors(ClassifyResult result, List<DocType> docTypes, List<MsgType> msgTypes)
        {
            var recTpId = msgTypes.FirstOrDefault(t => t.MsgTpCode == result.MsgTpCode)?.RecTpId;
            if (recTpId is null) return;

            var anchorTypes = docTypes.Where(d => d.RecTpId == recTpId).Select(d => d.DocTpId).ToHashSet();
            foreach (var pkg in result.Packages.Where(p => !p.Attachments.Any(a => a.IsPackage)))
            {
                var anchor = pkg.Attachments.FirstOrDefault(a => a.DocTpId is int docTpId && anchorTypes.Contains(docTpId));
                if (anchor is not null) anchor.IsPackage = true;
            }
        }
        #endregion

        #region Private processing methods
        private async Task<(Result<ClassifyResult> Result, List<int?> PartyIds, PartyType? PartyType)> ClassifyMessageAsync(ApplicationExt app, List<PartyType> partyTypes, InternetMessage message, int senderId)
        {
            var subType = app.MsgTypes.FirstOrDefault(t => t.MsgTpCode == "SUB");
            var hasLinkGate = subType?.IsApprRequired ?? false;

            var partyType = hasLinkGate ? partyTypes.FirstOrDefault(p => p.PartyTpId == subType!.PartyTpId) : null;

            List<SenderHistory> history = [];
            var partyIds = partyType is not null ? await mailDataSvc.GetSenderPartyIdsAsync(senderId, partyType.PartyTpId) : [];
            var isKnownSender = partyIds.Count > 0;

            var content = new List<ClaudeContentPart>
            {
                new("text/plain", Text: $"From: {message.FromAddr}\nSubject: {message.Subject}\n\n{message.Body}"),
                new("text/plain", Text: $"Attachments: {(message.Attachments.Count > 0 ? string.Join(", ", message.Attachments.Select(a => a.FileName)) : "none")}")
            };

            if (partyType is not null)
            {
                var linked = history.Select(h => (h.PartyId, h.PartyName)).Distinct().ToList();
                var linkedText = linked.Count > 0 ? string.Join("; ", linked.Select(v => $"{partyType.PartyTpKeyField} {v.PartyId} ({v.PartyName})")) : "This sender is not linked to anything yet.";
                content.Add(new ClaudeContentPart("text/plain", Text: $"{partyType.PartyTpKeyField} already linked to this sender: {linkedText}"));
            }

            var includeExtraction = !hasLinkGate || isKnownSender;
            if (includeExtraction)
                content.AddRange(message.Attachments.Select(a => new ClaudeContentPart(a.MediaType, Data: a.Bytes)));

            var msgTypeOptions = app.MsgTypes.Select(t => new ClaudeClassification { Id = t.MsgTpId, Code = t.MsgTpCode, Name = t.MsgTpDesc, Desc = t.ClassifyHint ?? t.MsgTpDesc }).ToList();
            var docTypeOptions = app.DocTypes.Select(d => new ClaudeClassification { Id = d.DocTpId, Code = d.DocTpCode, Name = d.DocTpName, Desc = d.DocTpDesc }).ToList();

            var result = await classifier.ClassifyEmailAsync(content, app.ProcessDesc, msgTypeOptions, docTypeOptions, app.FieldsByMsgType, includeExtraction);
            return (result, partyIds, partyType);
        }
        private async Task CheckAndRequestMissingDocsAsync(IMailService mail, InternetMessage message, string fromAddr, int msgNo, string tasksHndName, List<int> pkgNos)
        {
            var tasks = await mailDataSvc.GetPkgTasksAsync(tasksHndName, pkgNos);

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
        private async Task ProcessSelfSentMessageAsync(Account account, InternetMessage message)
        {
            var msgResult = await LogMessageAsync(account, message);

            if (msgResult.IsFailure) return;

            await FinalizeAsync(new MessageFinalization { MsgNo = msgResult.Value.MsgNo, ResTpCode = "NOOP", MsgResult = "Message from the account's own mailbox address, not a real submission." });
            Tracker.Track($"MsgNo {msgResult.Value.MsgNo}: message from the account's own address, logged and skipped.");
        }
        private async Task ProcessClassifiedMessageAsync(Account account, IMailService mail, InternetMessage message, int msgNo, int senderId, ApplicationExt app, ClassifyResult result, bool saveContext)
        {
            var context = saveContext ? result : null;

            if (result.MsgTpCode is null)
            {
                await mail.ForwardAsync(message.ProvMsgId, account.AdmAcctEMail, "This message could not be classified and needs manual review.");
                await FinalizeAsync(new MessageFinalization { MsgNo = msgNo, MsgContext = context, ResTpCode = "REVIEW", MsgResult = "Could not determine message type." });
                Tracker.Track($"MsgNo {msgNo}: could not classify message, flagged for review.");
                return;
            }

            var msgType = app.MsgTypes.FirstOrDefault(t => t.MsgTpCode == result.MsgTpCode);

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

                var fields = app.FieldsByMsgType.GetValueOrDefault(msgType.MsgTpId) ?? [];
                var keyFields = fields.Where(f => f.IsKey && f.FieldMode != "Searchable").ToList();
                var pkgFields = result.Packages.FirstOrDefault()?.Fields;

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

            var attachmentTuples = message.Attachments.Select(a => (a.FileName, a.Bytes, a.MediaType)).ToList();
            var (pkgDocs, unlinkedDocNos) = await filer.SavePackagesAsync(msgNo, app, account.OwnerUId, msgType, result.MsgTpCode, result.Packages, attachmentTuples);
            var items = pkgDocs.Select(c => new MessageItem { MsgNo = msgNo, PkgNo = c.PkgNo, DocNo = c.DocNo }).ToList();

            if (items.Count > 0)
                await mailDataSvc.SaveMessageItemsAsync(items);

            var pkgNosCreated = items.Select(i => i.PkgNo).Distinct().ToList();

            if (unlinkedDocNos.Count > 0)
            {
                var docNoList = string.Join(", ", unlinkedDocNos);
                await mail.ForwardAsync(message.ProvMsgId, account.AdmAcctEMail, $"DocNo(s) {docNoList} were saved but could not be linked to a package and need manual review.");
                await FinalizeAsync(new MessageFinalization { MsgNo = msgNo, MsgContext = context, MsgTpCode = result.MsgTpCode, ResTpCode = "REVIEW", MsgResult = $"DocNo(s) {docNoList} could not be linked to a package." });
                Tracker.Notify($"MsgNo {msgNo}: DocNo(s) {docNoList} could not be linked to a package");
                return;
            }
            var replyBody = msgType.ReplyMessage?.Replace("{numPkgs}", pkgNosCreated.Count.ToString());

            if (pkgNosCreated.Count > 0)
            {
                if (!string.IsNullOrEmpty(replyBody))
                    await mail.ReplyAsync(message.ProvMsgId, replyBody);

                await FinalizeAsync(new MessageFinalization { MsgNo = msgNo, MsgContext = context, MsgTpCode = result.MsgTpCode, ResTpCode = "OK" });

                if (msgType.IsTaskCheckRequired && app.OnTasksGet is string tasksHndName)
                    await CheckAndRequestMissingDocsAsync(mail, message, message.FromAddr, msgNo, tasksHndName, pkgNosCreated);
            }
            else
            {
                await mail.ForwardAsync(message.ProvMsgId, account.AdmAcctEMail, "No packages could be created from this message -- see the log for details.");
                await FinalizeAsync(new MessageFinalization { MsgNo = msgNo, MsgContext = context, MsgTpCode = result.MsgTpCode, ResTpCode = "REVIEW", MsgResult = "No packages created from this message." });
                Tracker.Track($"MsgNo {msgNo}: no packages created from this message, admin notified.");
            }
        }
        private async Task ProcessInboundMessageAsync(Account account, IMailService mail, ApplicationExt app, List<PartyType> partyTypes, InternetMessage message)
        {
            var msgResult = await LogMessageAsync(account, message);

            if (msgResult.IsFailure)
            {
                Tracker.Track($"{message.FromAddr}: message already logged, skipping.");
                return;
            }

            var msgNo = msgResult.Value.MsgNo;
            var senderId = msgResult.Value.SenderId;

            Tracker.Track($"MsgNo {msgNo}: classifying message from {message.FromAddr} with {message.Attachments.Count} attachment(s)...");
            var (classifyResult, partyIds, partyType) = await ClassifyMessageAsync(app, partyTypes, message, senderId);

            if (classifyResult.IsFailure)
            {
                await mail.ForwardAsync(message.ProvMsgId, account.AdmAcctEMail, "This message could not be classified and needs manual review.");
                await FinalizeAsync(new MessageFinalization { MsgNo = msgNo, ResTpCode = "REVIEW", MsgResult = $"Claude classification failed: {classifyResult.Message}" });
                Tracker.Track($"MsgNo {msgNo}: Claude classification failed ({classifyResult.Message}).");
                return;
            }

            var result = classifyResult.Value;
            AssignAnchors(result, app.DocTypes, app.MsgTypes);
            var pkgFields = result.Packages.FirstOrDefault()?.Fields;
            var candidatePartyId = partyType is not null ? GetFieldInt(pkgFields, partyType.PartyTpKeyField) : null;
            var classifiedType = app.MsgTypes.FirstOrDefault(t => t.MsgTpCode == result.MsgTpCode);
            var isGated = partyType is not null && (classifiedType?.IsApprRequired ?? false);
            var isLinked = !isGated || partyIds.Contains(candidatePartyId) || partyIds.Contains(null);

            if (!isLinked)
            {
                var isKnownSender = partyIds.Count > 0;

                var openReq = await mailDataSvc.GetOpenRequestAsync(senderId, partyType!.PartyTpId);
                var isReminderDue = openReq is not null && openReq.ReqDT <= DateTime.UtcNow.AddDays(-ReminderThresholdDays);

                var missingInfo = GetMissingInfo(result, partyType, isKnownSender);

                string? noticeMsgId = message.InternetMessageId;

                if (openReq is null)
                {
                    var comment = GetApprovalComment(result, partyType, isKnownSender, missingInfo);
                    noticeMsgId = await mail.SendApprovalRequestAsync(message.ProvMsgId, account.AdmAcctEMail, comment);
                }
                else if (isReminderDue)
                {
                    var comment = GetApprovalComment(result, partyType, isKnownSender, missingInfo, openReq.NumItems, openReq.ReqDT);
                    var orig = await mail.GetMessageByIdAsync(openReq.MsgId);
                    if (orig is not null)
                        await mail.ReplyAsync(orig.ProvMsgId, comment);
                }

                var candidatePartyName = partyType is not null ? GetFieldString(pkgFields, partyType.PartyTpNameField) : null;
                var saveResult = await mailDataSvc.SaveApprRequestAsync(new { SenderId = senderId, PartyTpId = partyType!.PartyTpId, PartyId = candidatePartyId, PartyName = candidatePartyName, MsgNo = msgNo, MsgId = noticeMsgId });
                if (saveResult.IsFailure)
                {
                    await FinalizeAsync(new MessageFinalization { MsgNo = msgNo, ResTpCode = "REVIEW", MsgResult = $"Failed to save approval request: {saveResult.Message}" });
                    Tracker.Track($"MsgNo {msgNo}: failed to save approval request ({saveResult.Message}).");
                    return;
                }

                var resTpCode = missingInfo.Count > 0 ? "PARTIAL" : "HELD";
                var msgResultText = missingInfo.Count > 0 ? string.Join("; ", missingInfo) : null;

                var logMsg = openReq is null
                    ? $"MsgNo {msgNo} ({message.FromAddr}): sender not linked to identified {partyType!.PartyTpKeyField}, held pending admin approval."
                    : isReminderDue
                        ? $"MsgNo {msgNo} ({message.FromAddr}): {openReq.NumItems} message(s) pending since {openReq.ReqDT:d}, reminder sent."
                        : $"MsgNo {msgNo} ({message.FromAddr}): already has a pending request for this {partyType!.PartyTpKeyField}, held silently.";

                await FinalizeAsync(new MessageFinalization { MsgNo = msgNo, MsgContext = result, MsgTpCode = result.MsgTpCode, ResTpCode = resTpCode, MsgResult = msgResultText });
                Tracker.Track(logMsg);
                return;
            }

            await ProcessClassifiedMessageAsync(account, mail, message, msgNo, senderId, app, result, saveContext: true);
        }
        private async Task ProcessInfoRequestReplyAsync(Account account, IMailService mail, InternetMessage message, InfoRequestBridge bridge, ApplicationExt app, string tasksHndName)
        {
            var msgResult = await LogMessageAsync(account, message);

            if (msgResult.IsFailure)
            {
                Tracker.Track($"{message.FromAddr}: RFI reply already logged, skipping.");
                return;
            }

            var msgNo = msgResult.Value.MsgNo;

            var attachments = message.Attachments;

            var replyText = GetPlainText(message.UniqueBody);
            await conv.AppendItemAsync(bridge.ConvNo, "user", $"{replyText}\nAttachments: {(attachments.Count > 0 ? string.Join(", ", attachments.Select(a => a.FileName)) : "none")}");

            var outstanding = (await mailDataSvc.GetPkgTasksAsync(tasksHndName, [bridge.PkgNo])).Where(t => !t.IsComplete).ToList();

            // Items collected across the whole reply and saved once below -- was one SaveMessageItemsAsync
            // call per attachment, now a single round trip regardless of how many attachments matched.
            var items = new List<MessageItem>();

            if (outstanding.Count > 0 && attachments.Count > 0)
            {
                var options = outstanding
                    .Select(t => app.DocTypes.FirstOrDefault(d => d.DocTpId == t.DocTypeId))
                    .Where(d => d is not null)
                    .Select(d => new ClaudeClassification { Id = d!.DocTpId, Code = d.DocTpCode, Name = d.DocTpName, Desc = d.DocTpDesc })
                    .ToList();

                foreach (var attachment in attachments)
                {
                    var classifyResult = await classifier.ClassifyAsync(attachment.Bytes, attachment.MediaType, app.ProcessDesc, options);

                    if (classifyResult.IsFailure || !options.Any(o => o.Id == classifyResult.Value.Id))
                    {
                        Tracker.Track($"IReqNo {bridge.IReqNo}: attachment {attachment.FileName} did not match an outstanding requirement, skipping.");
                        continue;
                    }

                    var attachResult = await filer.AttachDocumentAsync(app, bridge.PkgNo, classifyResult.Value.Id, attachment.Bytes, attachment.FileName);
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

            var remaining = (await mailDataSvc.GetPkgTasksAsync(tasksHndName, [bridge.PkgNo])).Where(t => !t.IsComplete).ToList();

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
        private async Task<HeldMessage> PrepareHeldMessageAsync(Account account, IMailService mail, ApprRequestItem heldMsg, ApplicationExt app, Party party)
        {
            var orig = await mail.GetMessageByIdAsync(heldMsg.MsgId);
            if (orig is null) return new HeldMessage(heldMsg, null, null, false, []);

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
            if (result is null) return new HeldMessage(heldMsg, orig, null, false, []);

            AssignAnchors(result, app.DocTypes, app.MsgTypes);
            var isExtracted = false;
            foreach (var anchor in result.Packages.SelectMany(p => p.Attachments).Where(a => a.IsPackage && a.ExtractedFields is null))
            {
                var file = orig.Attachments.FirstOrDefault(a => a.FileName == anchor.FileName);
                if (file is null) continue;

                var extracted = await filer.ClassifyAndExtractAsync(app, file.Bytes, file.MediaType, file.FileName);
                if (extracted.IsFailure)
                {
                    Tracker.Track($"MsgNo {heldMsg.MsgNo}: extraction of {file.FileName} failed ({extracted.Message}).");
                    continue;
                }

                anchor.DocTpId = extracted.Value.DocType.DocTpId;
                anchor.ExtractedFields = extracted.Value.Fields;
                isExtracted = true;
            }

            var recTpId = app.MsgTypes.FirstOrDefault(t => t.MsgTpCode == result.MsgTpCode)?.RecTpId;
            foreach (var pkg in result.Packages.Where(p => !p.Attachments.Any(a => a.IsPackage)))
            {
                foreach (var candidate in pkg.Attachments.Where(a => a.DocTpId is null))
                {
                    var file = orig.Attachments.FirstOrDefault(a => a.FileName == candidate.FileName);
                    if (file is null) continue;

                    var extracted = await filer.ClassifyAndExtractAsync(app, file.Bytes, file.MediaType, file.FileName);
                    if (extracted.IsFailure)
                    {
                        Tracker.Track($"MsgNo {heldMsg.MsgNo}: extraction of {file.FileName} failed ({extracted.Message}).");
                        continue;
                    }

                    candidate.DocTpId = extracted.Value.DocType.DocTpId;
                    isExtracted = true;
                    if (extracted.Value.DocType.RecTpId != recTpId) continue;

                    candidate.IsPackage = true;
                    candidate.ExtractedFields = extracted.Value.Fields;
                    break;
                }
            }
            var isVerified = !result.Packages.SelectMany(p => p.Attachments).Any(a => a.IsPackage && a.ExtractedFields is null);

            var label = party.Type.PartyTpName.ToLower();
            var mismatches = new List<string>();
            foreach (var pkg in result.Packages)
            {
                var anchor = pkg.Attachments.FirstOrDefault(a => a.IsPackage && a.ExtractedFields is not null);
                var source = anchor?.ExtractedFields ?? pkg.Fields;

                var foundId = GetFieldInt(source, party.Type.PartyTpKeyField);
                if (foundId == party.Id) continue;

                var foundName = GetFieldString(source, party.Type.PartyTpNameField);

                if (foundId is null)
                {
                    if (anchor is null) continue;
                    mismatches.Add(foundName is null
                        ? $"I could not identify a {label} in {anchor.FileName}."
                        : $"I could not identify a {label} in {anchor.FileName}. It names \"{foundName}\", which is not on file.");
                    anchor.DocTpId = null;
                    anchor.ExtractedFields = null;
                    continue;
                }

                var docName = anchor is not null ? anchor.FileName : "the email text";
                mismatches.Add($"The {label} you provided {party.Name ?? "(no name)"} with {party.Type.PartyTpKeyField} {party.Id} does not match what I found in {docName}. I found {foundName ?? "(no name)"} with {party.Type.PartyTpKeyField} {foundId}.");
            }

            if (isExtracted)
                await mailDataSvc.SaveMessageContextAsync(heldMsg.MsgNo, result);

            return new HeldMessage(heldMsg, orig, result, isVerified, mismatches);
        }
        private async Task ReprocessHeldMessageAsync(Account account, IMailService mail, HeldMessage held, int senderId, Party party, ApplicationExt app)
        {
            var msgNo = held.Item.MsgNo;

            if (held.Orig is null)
            {
                await FinalizeAsync(new MessageFinalization { MsgNo = msgNo, ResTpCode = "FAILED", MsgResult = "Approved but original message could not be refetched from Inbox." });
                Tracker.Track($"MsgNo {msgNo}: approved but original message could not be refetched from Inbox.");
                return;
            }

            if (held.Result is null)
            {
                await mail.ForwardAsync(held.Orig.ProvMsgId, account.AdmAcctEMail, "This message's saved classification could not be read and needs manual review.");
                await FinalizeAsync(new MessageFinalization { MsgNo = msgNo, ResTpCode = "REVIEW", MsgResult = "Saved MsgContext missing or unreadable on reprocess." });
                Tracker.Track($"MsgNo {msgNo}: saved MsgContext missing or unreadable on reprocess.");
                return;
            }

            if (!held.IsVerified)
            {
                await mail.ForwardAsync(held.Orig.ProvMsgId, account.AdmAcctEMail, $"An attachment on this message could not be read to confirm the {party.Type.PartyTpName.ToLower()}, so it was not processed and needs manual review.");
                await FinalizeAsync(new MessageFinalization { MsgNo = msgNo, ResTpCode = "REVIEW", MsgResult = "Anchor could not be extracted to verify party." });
                Tracker.Track($"MsgNo {msgNo}: anchor could not be extracted to verify party, sent to review.");
                return;
            }

            foreach (var pkg in held.Result.Packages)
            {
                FillPartyFields(pkg.Fields, party);
                foreach (var anchor in pkg.Attachments.Where(a => a.IsPackage && a.ExtractedFields is not null))
                    FillPartyFields(anchor.ExtractedFields!, party);
            }

            await ProcessClassifiedMessageAsync(account, mail, held.Orig, msgNo, senderId, app, held.Result, saveContext: false);
        }
        private async Task ProcessAdminReplyAsync(Account account, IMailService mail, ApplicationExt app, List<PartyType> partyTypes, InternetMessage message)
        {
            var msgResult = await LogMessageAsync(account, message);

            if (msgResult.IsFailure)
            {
                Tracker.Track($"{message.FromAddr}: admin message already logged, skipping.");
                return;
            }

            var adminMsgNo = msgResult.Value.MsgNo;

            var req = await mailDataSvc.GetApprRequestAsync(message.BridgeMsgIds);

            if (req is null)
            {
                await mail.ReplyAsync(message.ProvMsgId, "This reply doesn't correspond to a request currently pending approval -- there's nothing to act on.");
                await FinalizeAsync(new MessageFinalization { MsgNo = adminMsgNo, MsgTpCode = "CMD", ResTpCode = "REVIEW", MsgResult = "No matching approval request found." });
                Tracker.Track($"Admin message from {message.FromAddr}: no matching approval request.");
                return;
            }

            Tracker.Track($"Admin reply from {message.FromAddr} matched ReqNo {req.ReqNo}.");

            var partyType = partyTypes.FirstOrDefault(p => p.PartyTpId == req.PartyTpId);

            if (partyType is null)
            {
                await mail.ReplyAsync(message.ProvMsgId, "Something went wrong processing that reply. Please contact support.");
                await FinalizeAsync(new MessageFinalization { MsgNo = adminMsgNo, MsgTpCode = "CMD", ResTpCode = "FAILED", MsgResult = $"PartyTpId {req.PartyTpId} not configured." });
                Tracker.Track($"ReqNo {req.ReqNo}: PartyTpId {req.PartyTpId} not configured.");
                return;
            }

            var label = partyType.PartyTpName.ToLower();

            var cmdResult = await cmdSvc.InterpretApprovalReplyAsync(message.Subject, GetPlainText(message.UniqueBody), partyType.PartyTpName, req.PartyName);

            if (cmdResult.IsFailure)
            {
                await mail.ReplyAsync(message.ProvMsgId, "Something went wrong interpreting that reply. Please try again.");
                await FinalizeAsync(new MessageFinalization { MsgNo = adminMsgNo, MsgTpCode = "CMD", ResTpCode = "FAILED", MsgResult = $"Claude command interpretation failed: {cmdResult.Message}" });
                Tracker.Track($"ReqNo {req.ReqNo}: Claude command interpretation failed ({cmdResult.Message}).");
                return;
            }

            if (cmdResult.Value.CmdCode != "APPROVE" && cmdResult.Value.CmdCode != "REJECT")
            {
                await mail.ReplyAsync(message.ProvMsgId, "Sorry, I couldn't understand that reply. Please reply APPROVE, REJECT, or the correct name.");
                await FinalizeAsync(new MessageFinalization { MsgNo = adminMsgNo, MsgTpCode = "CMD", ResTpCode = "REVIEW", MsgResult = "Admin reply did not resolve to APPROVE/REJECT." });
                Tracker.Track($"ReqNo {req.ReqNo}: admin reply did not resolve to APPROVE/REJECT, left pending.");
                return;
            }

            var isApproved = cmdResult.Value.CmdCode == "APPROVE";
            var namedParty = cmdResult.Value.PartyName;

            int? partyId = req.PartyId;
            var partyName = req.PartyName;

            if (isApproved && namedParty is not null && (partyId is null || !string.Equals(namedParty, req.PartyName, StringComparison.OrdinalIgnoreCase)))
            {
                List<Dictionary<string, object>> matches = partyType.LookupHndName is not null ? await filerDataSvc.GetLookupAsync(partyType.LookupHndName, namedParty) : [];
                if (matches.Count != 1)
                {
                    await mail.ReplyAsync(message.ProvMsgId, $"{partyType.PartyTpName} \"{namedParty}\" not found. Please reply with the exact name.");
                    await FinalizeAsync(new MessageFinalization { MsgNo = adminMsgNo, MsgTpCode = "CMD", ResTpCode = "REVIEW", MsgResult = $"\"{namedParty}\" not found (or ambiguous)." });
                    Tracker.Track($"ReqNo {req.ReqNo}: \"{namedParty}\" not found (or ambiguous), left pending.");
                    return;
                }
                partyId = Convert.ToInt32(matches[0][partyType.PartyTpKeyField]);
                partyName = matches[0][partyType.PartyTpNameField]?.ToString();
            }

            if (isApproved && partyId is null)
            {
                await mail.ReplyAsync(message.ProvMsgId, $"Please reply with the name of the {label} to link this sender to.");
                await FinalizeAsync(new MessageFinalization { MsgNo = adminMsgNo, MsgTpCode = "CMD", ResTpCode = "REVIEW", MsgResult = "No party could be resolved." });
                Tracker.Track($"ReqNo {req.ReqNo}: no party could be resolved, left pending.");
                return;
            }

            var pending = await mailDataSvc.GetApprRequestItemsAsync(req.ReqNo);

            var prepared = new List<HeldMessage>();
            if (isApproved && partyId is int approvedId)
            {
                foreach (var heldMsg in pending)
                    prepared.Add(await PrepareHeldMessageAsync(account, mail, heldMsg, app, new Party(partyType, approvedId, partyName)));

                var mismatches = prepared.SelectMany(p => p.Mismatches).ToList();
                if (mismatches.Count > 0)
                {
                    await mail.ReplyAsync(message.ProvMsgId, $"{string.Join("\n\n", mismatches)}\n\nPlease reply with the correct {label} name (add it to the {label} list first if it's new), or REJECT to decline.");
                    await FinalizeAsync(new MessageFinalization { MsgNo = adminMsgNo, MsgTpCode = "CMD", ResTpCode = "REVIEW", MsgResult = $"{mismatches.Count} party mismatch(es) -- approval not saved." });
                    Tracker.Track($"ReqNo {req.ReqNo}: {mismatches.Count} party mismatch(es), approval not saved, left pending.");
                    return;
                }
            }

            var resolveResult = await mailDataSvc.SaveMessageResolutionAsync(new ApprResolution
            {
                ReqNo = req.ReqNo,
                PartyId = partyId,
                IsApproved = isApproved,
                CmdCode = cmdResult.Value.CmdCode!,
                AdminMsgNo = adminMsgNo,
                Reference = $"SenderId {req.SenderId}, PartyId {partyId}",
                ResultCode = 0,
                ResultMsg = isApproved ? "Approved" : "Rejected"
            });

            if (resolveResult.IsFailure)
            {
                await mail.ReplyAsync(message.ProvMsgId, "Something went wrong recording that decision. Please try again or contact support.");
                await FinalizeAsync(new MessageFinalization { MsgNo = adminMsgNo, MsgTpCode = "CMD", ResTpCode = "FAILED", MsgResult = $"Failed to save resolution: {resolveResult.Message}" });
                Tracker.Track($"ReqNo {req.ReqNo}: failed to save resolution ({resolveResult.Message}).");
                return;
            }

            Tracker.Track($"{message.FromAddr}: {(isApproved ? "approved" : "rejected")} ReqNo {req.ReqNo} (PartyId {partyId}) -- {pending.Count} message(s) affected.");

            if (!isApproved)
                foreach (var heldMsg in pending)
                    await FinalizeAsync(new MessageFinalization { MsgNo = heldMsg.MsgNo, ResTpCode = "REJECTED", MsgResult = "Sender rejected by administrator." });
            else if (partyId is int linkedId)
                foreach (var held in prepared)
                    await ReprocessHeldMessageAsync(account, mail, held, req.SenderId, new Party(partyType, linkedId, partyName), app);

            await mail.ReplyAsync(message.ProvMsgId, isApproved
                ? $"Your approval was processed. Sender {req.FromAddr} was linked to {partyName}. {pending.Count} pending message(s) will be processed. Please check for individual results."
                : $"Rejected. Sender {req.FromAddr} was not linked. {pending.Count} pending message(s) were declined.");

            await FinalizeAsync(new MessageFinalization { MsgNo = adminMsgNo, MsgTpCode = "CMD", ResTpCode = "OK" });
        }
        private async Task FinalizeAsync(MessageFinalization finalize)
        {
            var result = await mailDataSvc.SaveMessageFinalizationAsync(finalize);
            if (result.IsFailure)
                Tracker.Track($"MsgNo {finalize.MsgNo}: failed to persist final status ({result.Message}) -- intended ResTpCode '{finalize.ResTpCode}' was NOT written.");
        }
        #endregion
    }
}
