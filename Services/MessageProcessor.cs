using Cheing;
using Cheing.Net.Ai;
using EMF.FilerSvc;
using EMF.FilerSvc.Models;
using EMF.Mail.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Graph.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FilerDataService = EMF.FilerSvc.Services.DataService;
using GraphMessage = Microsoft.Graph.Models.Message;

namespace EMF.Mail.Services
{
    public class MessageProcessor(IConfiguration config, DataService mailDataSvc, FilerDataService filerDataSvc, TriageService triageSvc, CommandService cmdSvc, ClaudeClassifier classifier, Filer filer, ConversationService conv)
    {
        private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

        // Runs vendor identification + SUB/INQ classification for a message. Always runs, regardless of the
        // sender's linked-vendor count -- see TriageService for why. Returns the sender's linked-vendor history
        // alongside the classify result since callers need both (approval check + the fan-out-relevant list).
        private async Task<(Result<TriageResult> Result, List<SenderHistory> History)> ClassifyMessageAsync(MailAccount account, List<DocType> docTypes, Dictionary<int, List<ClaudeFieldSpec>> fieldsByDocType, string processDesc, GraphMessage message, int senderId)
        {
            var history = await mailDataSvc.GetSenderHistoryAsync(account.GetSenderHistHndName, senderId, account.AppId);
            var attachments = (message.Attachments?.OfType<FileAttachment>() ?? [])
                .Where(a => a.ContentBytes is not null)
                .Select(a => new AttachmentContent(a.Name ?? "", a.ContentBytes!, Filer.GetMediaType(a.Name ?? "")))
                .ToList();
            var body = message.Body?.Content ?? "";
            var fromAddr = message.From?.EmailAddress?.Address ?? "";

            var result = await triageSvc.ClassifyAsync(message.Subject ?? "", body, fromAddr, attachments, docTypes, history, processDesc, fieldsByDocType);
            return (result, history);
        }

        // Plain C# template, not Claude-composed -- the ask's exact wording doesn't matter (see ProjectContext),
        // only that a reply correctly bridges back and gets interpreted. Only outstanding (not IsComplete)
        // gaps are listed; already-satisfied ones from the same gap-check call are filtered out by the caller.
        private static string ComposeInfoRequestBody(List<PkgTask> gaps) =>
            "We still need the following before this request can proceed: " + string.Join("; ", gaps.Select(g => g.Task)) + ".";

        // Audit-log-only plain text -- ai.TblConvItems is never replayed into a live Claude call for this
        // flow (see ProjectContext), so a lossy strip-and-collapse is fine; no need to preserve formatting.
        private static string GetPlainText(string html)
        {
            var stripped = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
            var decoded = System.Net.WebUtility.HtmlDecode(stripped);
            return System.Text.RegularExpressions.Regex.Replace(decoded, @"\s+", " ").Trim();
        }

        // Runs right after a submission's items are saved -- one gap-check call covers every PkgNo the
        // submission created (ap.sprTblGetTasks accepts a list). One msg.TblInfoRequests row and one email
        // per PkgNo with outstanding gaps, not one combined email for the whole submission -- keeps the
        // bridge match unambiguous (rfibridge's SentMsgId lookup resolves to exactly one row) without needing
        // to work out which package a reply is about when a submission created more than one.
        private async Task CheckAndRequestMissingDocsAsync(GraphMailService mail, GraphMessage message, string fromAddr, int msgNo, List<int> pkgNos)
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

                var body = ComposeInfoRequestBody(gaps);
                var sentMsgId = await mail.SendInfoRequestAsync(message.Id!, body);
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

        // Acts on an already-classified message -- shared by the immediate path (classification just ran as
        // part of the hold decision, VendId matched a linked vendor) and the post-approval fan-out path
        // (classification is reused from the original hold's saved MsgContext, not re-run). saveContext is
        // false on the fan-out path -- MsgContext was already written when the message was first held, so
        // the finalize call there only needs to touch MsgTpId/ResTpId/IsProcessed, not resave it.
        private async Task ProcessClassifiedMessageAsync(MailAccount account, GraphMailService mail, GraphMessage message, int msgNo, int senderId, TriageResult triageResult, bool saveContext)
        {
            var context = saveContext ? triageResult : null;

            if (triageResult.MsgTpCode is null)
            {
                await mail.MarkNeedsReviewAsync(message.Id!);
                await mail.ForwardAsync(message.Id!, account.AdmAcctEMail, "This message could not be classified and needs manual review.");
                await mailDataSvc.FinalizeMessageAsync(new MessageFinalize { MsgNo = msgNo, MsgContext = context, ResTpCode = "REVIEW", MsgResult = "Could not determine message type." });
                Tracker.Track($"MsgNo {msgNo}: could not classify message, flagged for review.");
                return;
            }

            if (triageResult.MsgTpCode == "INQ")
            {
                var matches = await mailDataSvc.GetReqStatusAsync(senderId, triageResult.InvcNbr ?? "");
                var reply = await triageSvc.ComposeInquiryReplyAsync(matches);

                if (reply.IsFailure)
                {
                    await mailDataSvc.FinalizeMessageAsync(new MessageFinalize { MsgNo = msgNo, MsgContext = context, MsgTpCode = triageResult.MsgTpCode, ResTpCode = "FAILED", MsgResult = $"Failed to compose inquiry reply: {reply.Message}" });
                    Tracker.Track($"MsgNo {msgNo}: failed to compose inquiry reply ({reply.Message}).");
                    return;
                }

                await mail.ReplyAsync(message.Id!, reply.Value);
                await mail.FlagProcessedAsync(message.Id!);
                await mailDataSvc.FinalizeMessageAsync(new MessageFinalize { MsgNo = msgNo, MsgContext = context, MsgTpCode = triageResult.MsgTpCode, ResTpCode = "OK", MsgResult = $"Inquiry reply sent ({matches.Count} match(es) found)." });
                Tracker.Track($"MsgNo {msgNo}: inquiry reply sent ({matches.Count} match(es) found).");
                return;
            }

            var items = new List<MessageItem>();
            var reqNos = new List<int>();

            foreach (var group in triageResult.Attachments.GroupBy(a => a.GroupId))
            {
                var processing = group.FirstOrDefault(a => a.Label == "Processing");
                if (processing is null)
                {
                    Tracker.Track($"MsgNo {msgNo}: group {group.Key} has no Processing attachment, skipping.");
                    continue;
                }

                var attachment = message.Attachments?.OfType<FileAttachment>().FirstOrDefault(a => a.Name == processing.FileName);
                if (attachment?.ContentBytes is null)
                {
                    Tracker.Track($"MsgNo {msgNo}: attachment {processing.FileName} not found or empty, skipping.");
                    continue;
                }

                var mediaType = Filer.GetMediaType(processing.FileName);

                // Already classified + extracted during triage (known sender, document image was already
                // open for vendor purposes) -- skip Filer reading the same document a second/third time.
                var result = processing.DocTpId is not null && processing.ExtractedFields is not null
                    ? await filer.SaveExtractedDocumentAsync(account.AppId, account.OwnerUId, processing.DocTpId.Value, processing.ExtractedFields, attachment.ContentBytes, processing.FileName)
                    : await filer.ProcessDocumentAsync(account.AppId, account.OwnerUId, attachment.ContentBytes, mediaType, processing.FileName);
                if (result.IsFailure)
                {
                    Tracker.Track($"MsgNo {msgNo}: {result.Message}");
                    continue;
                }

                items.Add(new MessageItem { MsgNo = msgNo, PkgNo = result.Value.PkgNo, DocNo = result.Value.DocNo });
                reqNos.Add(result.Value.ReqNo);
                Tracker.Track($"MsgNo {msgNo}: created ReqNo {result.Value.ReqNo} (PkgNo {result.Value.PkgNo}) from {processing.FileName}.");

                foreach (var supporting in group.Where(a => a.Label == "Supporting"))
                {
                    var suppAttachment = message.Attachments?.OfType<FileAttachment>().FirstOrDefault(a => a.Name == supporting.FileName);
                    if (suppAttachment?.ContentBytes is null)
                    {
                        Tracker.Track($"MsgNo {msgNo}: supporting attachment {supporting.FileName} not found or empty, skipping.");
                        continue;
                    }

                    var attachResult = supporting.DocTpId is not null
                        ? await filer.AttachDocumentAsync(account.AppId, result.Value.PkgNo, supporting.DocTpId.Value, suppAttachment.ContentBytes, supporting.FileName)
                        : await filer.AttachDocumentAsync(result.Value.PkgNo, suppAttachment.ContentBytes, supporting.FileName);
                    if (attachResult.IsFailure)
                    {
                        Tracker.Track($"MsgNo {msgNo}: {attachResult.Message}");
                        continue;
                    }

                    items.Add(new MessageItem { MsgNo = msgNo, PkgNo = result.Value.PkgNo, DocNo = attachResult.Value });
                }
            }

            if (items.Count > 0)
                await mailDataSvc.SaveMessageItemsAsync(items);

            if (reqNos.Count > 0)
            {
                await mail.FlagProcessedAsync(message.Id!);
                await mail.ReplyAsync(message.Id!, $"Your request(s) were registered. Reference number(s): {string.Join(", ", reqNos)}");
                await mailDataSvc.FinalizeMessageAsync(new MessageFinalize { MsgNo = msgNo, MsgContext = context, MsgTpCode = triageResult.MsgTpCode, ResTpCode = "OK" });

                var fromAddr = message.From?.EmailAddress?.Address ?? "";
                var pkgNos = items.Select(i => i.PkgNo).Distinct().ToList();
                await CheckAndRequestMissingDocsAsync(mail, message, fromAddr, msgNo, pkgNos);
            }
            else
            {
                await mailDataSvc.FinalizeMessageAsync(new MessageFinalize { MsgNo = msgNo, MsgContext = context, MsgTpCode = triageResult.MsgTpCode, ResTpCode = "NOOP", MsgResult = "No requests created from this message." });
                Tracker.Track($"MsgNo {msgNo}: no requests created from this message.");
            }
        }

        // Handles one inbound (non-admin) message: log it, classify it, then either hold for approval
        // or process it immediately depending on the sender's vendor link.
        private async Task ProcessInboundMessageAsync(MailAccount account, GraphMailService mail, List<DocType> docTypes, Dictionary<int, List<ClaudeFieldSpec>> fieldsByDocType, string processDesc, GraphMessage message)
        {
            var fromAddr = message.From?.EmailAddress?.Address ?? "";
            var msgId = message.InternetMessageId ?? message.Id!;

            var msgResult = await mailDataSvc.SaveMessageAsync(new MailMessage
            {
                AcctId = account.AcctId,
                MsgId = msgId,
                FromAddr = fromAddr,
                FromName = message.From?.EmailAddress?.Name ?? "",
                RcptDate = message.ReceivedDateTime?.DateTime ?? DateTime.UtcNow,
                Subject = message.Subject ?? "",
                OrigMsgId = GraphMailService.GetOrigMsgId(message)
            });

            if (msgResult.IsFailure)
            {
                if (msgResult.Code == "2627")
                    Tracker.Track($"{fromAddr}: message already logged, skipping.");
                else
                    Tracker.Track($"{fromAddr}: failed to log message ({msgResult.Message}).");
                return;
            }

            var msgNo = msgResult.Value.MsgNo;
            var senderId = msgResult.Value.SenderId;

            var (classifyResult, history) = await ClassifyMessageAsync(account, docTypes, fieldsByDocType, processDesc, message, senderId);

            if (classifyResult.IsFailure)
            {
                await mail.MarkNeedsReviewAsync(message.Id!);
                await mail.ForwardAsync(message.Id!, account.AdmAcctEMail, "This message could not be classified and needs manual review.");
                await mailDataSvc.FinalizeMessageAsync(new MessageFinalize { MsgNo = msgNo, ResTpCode = "REVIEW", MsgResult = $"Claude classification failed: {classifyResult.Message}" });
                Tracker.Track($"MsgNo {msgNo}: Claude classification failed ({classifyResult.Message}).");
                return;
            }

            var triageResult = classifyResult.Value;
            var isLinked = triageResult.VendId is not null && history.Any(h => h.VendId == triageResult.VendId);

            if (!isLinked)
            {
                var isKnownSender = history.Count > 0;

                // A sender's second (or Nth) message naming the same not-yet-approved vendor gets held silently
                // -- only the first pending message for that (sender, vendor) pair triggers the forward to admin.
                // VendId is passed through as-is (possibly null) -- the SQL side matches a held row with an
                // unresolved VendId regardless, so a first-contact sender's own duplicate holds still dedup.
                var pending = await mailDataSvc.GetHeldMessagesAsync(senderId, triageResult.VendId, msgNo);

                string? fwdMsgId = null;

                // Computed once, reused for both the admin email and the DB write below -- avoids running the
                // same gap check twice and keeps the two from ever disagreeing with each other.
                var missingInfo = GetMissingInfo(triageResult, isKnownSender);

                if (pending.Count == 0)
                    fwdMsgId = await mail.SendApprovalRequestAsync(message.Id!, account.AdmAcctEMail, GetApprovalComment(triageResult, isKnownSender, missingInfo));

                var resTpCode = missingInfo.Count > 0 ? "PARTIAL" : "HELD";
                var msgResultText = missingInfo.Count > 0 ? string.Join("; ", missingInfo) : null;

                var logMsg = pending.Count == 0
                    ? $"MsgNo {msgNo} ({fromAddr}): sender not linked to identified vendor, held pending admin approval."
                    : $"MsgNo {msgNo} ({fromAddr}): already has a pending request for this vendor, held silently.";

                await mailDataSvc.FinalizeMessageAsync(new MessageFinalize { MsgNo = msgNo, MsgContext = triageResult, MsgTpCode = triageResult.MsgTpCode, IsHeld = true, FwdMsgId = fwdMsgId, ResTpCode = resTpCode, MsgResult = msgResultText });
                Tracker.Track(logMsg);
                return;
            }

            await ProcessClassifiedMessageAsync(account, mail, message, msgNo, senderId, triageResult, saveContext: true);
        }

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
        // pass may add one back.
        private async Task ProcessInfoRequestReplyAsync(MailAccount account, GraphMailService mail, GraphMessage message, RfiBridgeResult bridge, List<DocType> docTypes, string processDesc)
        {
            var fromAddr = message.From?.EmailAddress?.Address ?? "";
            var msgId = message.InternetMessageId ?? message.Id!;

            var msgResult = await mailDataSvc.SaveMessageAsync(new MailMessage
            {
                AcctId = account.AcctId,
                MsgId = msgId,
                FromAddr = fromAddr,
                FromName = message.From?.EmailAddress?.Name ?? "",
                RcptDate = message.ReceivedDateTime?.DateTime ?? DateTime.UtcNow,
                Subject = message.Subject ?? "",
                OrigMsgId = GraphMailService.GetOrigMsgId(message)
            });

            if (msgResult.IsFailure)
            {
                if (msgResult.Code == "2627")
                    Tracker.Track($"{fromAddr}: RFI reply already logged, skipping.");
                else
                    Tracker.Track($"{fromAddr}: failed to log RFI reply ({msgResult.Message}).");
                return;
            }

            var msgNo = msgResult.Value.MsgNo;

            var attachments = (message.Attachments?.OfType<FileAttachment>() ?? [])
                .Where(a => a.ContentBytes is not null)
                .ToList();

            var replyText = GetPlainText(message.UniqueBody?.Content ?? message.Body?.Content ?? "");
            await conv.AppendItemAsync(bridge.ConvNo, "user", $"{replyText}\nAttachments: {(attachments.Count > 0 ? string.Join(", ", attachments.Select(a => a.Name)) : "none")}");

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
                    var mediaType = Filer.GetMediaType(attachment.Name ?? "");
                    var classifyResult = await classifier.ClassifyAsync(attachment.ContentBytes!, mediaType, processDesc, options);

                    if (classifyResult.IsFailure || !options.Any(o => o.Id == classifyResult.Value.Id))
                    {
                        Tracker.Track($"IReqNo {bridge.IReqNo}: attachment {attachment.Name} did not match an outstanding requirement, skipping.");
                        continue;
                    }

                    var attachResult = await filer.AttachDocumentAsync(account.AppId, bridge.PkgNo, classifyResult.Value.Id, attachment.ContentBytes!, attachment.Name ?? "");
                    if (attachResult.IsFailure)
                    {
                        Tracker.Track($"IReqNo {bridge.IReqNo}: failed to attach {attachment.Name} ({attachResult.Message}).");
                        continue;
                    }

                    items.Add(new MessageItem { MsgNo = msgNo, PkgNo = bridge.PkgNo, DocNo = attachResult.Value });
                    Tracker.Track($"IReqNo {bridge.IReqNo}: attached {attachment.Name} as DocTypeId {classifyResult.Value.Id}.");
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
                await mail.ForwardAsync(message.Id!, account.AdmAcctEMail, $"RFI reply processed for PkgNo {bridge.PkgNo}: all required items received.");
                await mailDataSvc.FinalizeMessageAsync(new MessageFinalize { MsgNo = msgNo, MsgTpCode = "SUB", IReqNo = bridge.IReqNo, ResTpCode = "OK", MsgResult = "RFI resolved, all items received." });
                Tracker.Track($"IReqNo {bridge.IReqNo}: all items received, closed.");
            }
            else
            {
                var stillMissing = string.Join("; ", remaining.Select(t => t.Task));
                await conv.AppendItemAsync(bridge.ConvNo, "assistant", $"Still missing after reply: {stillMissing}.");
                await mail.ForwardAsync(message.Id!, account.AdmAcctEMail, $"RFI reply processed for PkgNo {bridge.PkgNo}, but {remaining.Count} item(s) still outstanding: {stillMissing}. This request will not be re-asked automatically -- please follow up.");
                await mailDataSvc.FinalizeMessageAsync(new MessageFinalize { MsgNo = msgNo, MsgTpCode = "SUB", IReqNo = bridge.IReqNo, ResTpCode = "REVIEW", MsgResult = $"{remaining.Count} item(s) still outstanding after RFI reply; closed without re-asking, admin notified." });
                Tracker.Track($"IReqNo {bridge.IReqNo}: {remaining.Count} item(s) still outstanding after reply, closed (no re-ask), admin notified.");
            }
        }

        // Reprocesses one previously-held message after its vendor link is approved -- reuses the
        // classification saved in MsgContext at hold time instead of re-running Claude.
        private async Task ReprocessHeldMessageAsync(MailAccount account, GraphMailService mail, HeldMessage heldMsg, int senderId)
        {
            var orig = await mail.GetMessageByIdAsync(heldMsg.MsgId);
            if (orig is null)
            {
                await mailDataSvc.FinalizeMessageAsync(new MessageFinalize { MsgNo = heldMsg.MsgNo, ResTpCode = "FAILED", MsgResult = "Approved but original message could not be refetched from Inbox." });
                Tracker.Track($"MsgNo {heldMsg.MsgNo}: approved but original message could not be refetched from Inbox.");
                return;
            }

            TriageResult? triageResult = null;
            try
            {
                if (heldMsg.MsgContext is not null)
                    triageResult = JsonSerializer.Deserialize<TriageResult>(heldMsg.MsgContext, _jsonOpts);
            }
            catch (JsonException)
            {
                triageResult = null;
            }

            if (triageResult is null)
            {
                await mail.MarkNeedsReviewAsync(orig.Id!);
                await mail.ForwardAsync(orig.Id!, account.AdmAcctEMail, "This message's saved classification could not be read and needs manual review.");
                await mailDataSvc.FinalizeMessageAsync(new MessageFinalize { MsgNo = heldMsg.MsgNo, ResTpCode = "REVIEW", MsgResult = "Saved MsgContext missing or unreadable on reprocess." });
                Tracker.Track($"MsgNo {heldMsg.MsgNo}: saved MsgContext missing or unreadable on reprocess.");
                return;
            }

            await ProcessClassifiedMessageAsync(account, mail, orig, heldMsg.MsgNo, senderId, triageResult, saveContext: false);
        }

        // Handles one admin reply: match it to a held message, interpret APPROVE/REJECT, act on it, and
        // fan out to every other message the sender has pending. Every admin message gets its own logged
        // row and a reply -- no path here leaves the admin without a response.
        private async Task ProcessAdminReplyAsync(MailAccount account, GraphMailService mail, GraphMessage message)
        {
            var fromAddr = message.From?.EmailAddress?.Address ?? "";
            var msgId = message.InternetMessageId ?? message.Id!;

            var msgResult = await mailDataSvc.SaveMessageAsync(new MailMessage
            {
                AcctId = account.AcctId,
                MsgId = msgId,
                FromAddr = fromAddr,
                FromName = message.From?.EmailAddress?.Name ?? "",
                RcptDate = message.ReceivedDateTime?.DateTime ?? DateTime.UtcNow,
                Subject = message.Subject ?? "",
                OrigMsgId = GraphMailService.GetOrigMsgId(message)
            });

            if (msgResult.IsFailure)
            {
                if (msgResult.Code == "2627")
                    Tracker.Track($"{fromAddr}: admin message already logged, skipping.");
                else
                    Tracker.Track($"{fromAddr}: failed to log admin message ({msgResult.Message}).");
                return;
            }

            var adminMsgNo = msgResult.Value.MsgNo;

            var bridgeMsgIds = GraphMailService.GetBridgeMsgIds(message);
            var held = (await mailDataSvc.GetHeldBridgeAsync(bridgeMsgIds)).FirstOrDefault();

            if (held is null)
            {
                await mail.ReplyAsync(message.Id!, "This reply doesn't correspond to a message currently on hold -- there's nothing to act on.");
                await mailDataSvc.FinalizeMessageAsync(new MessageFinalize { MsgNo = adminMsgNo, MsgTpCode = "CMD", ResTpCode = "REVIEW", MsgResult = "No matching hold found." });
                Tracker.Track($"Admin message from {fromAddr}: no matching hold.");
                return;
            }

            Tracker.Track($"Admin reply from {fromAddr} matched held MsgNo {held.MsgNo}.");

            var cmdResult = await cmdSvc.InterpretApprovalReplyAsync(message.Subject ?? "", message.UniqueBody?.Content ?? message.Body?.Content ?? "", held.CandVendName);

            if (cmdResult.IsFailure)
            {
                await mail.ReplyAsync(message.Id!, "Something went wrong interpreting that reply. Please try again.");
                await mailDataSvc.FinalizeMessageAsync(new MessageFinalize { MsgNo = adminMsgNo, MsgTpCode = "CMD", ResTpCode = "FAILED", MsgResult = $"Claude command interpretation failed: {cmdResult.Message}" });
                Tracker.Track($"MsgNo {held.MsgNo}: Claude command interpretation failed ({cmdResult.Message}).");
                return;
            }

            // Explicit both ways -- anything that isn't exactly APPROVE or REJECT (including null) is
            // treated as "didn't understand," never silently folded into a REJECT.
            if (cmdResult.Value.CmdCode != "APPROVE" && cmdResult.Value.CmdCode != "REJECT")
            {
                await mail.ReplyAsync(message.Id!, "Sorry, I couldn't understand that reply. Please reply APPROVE, REJECT, or the correct vendor name.");
                await mailDataSvc.FinalizeMessageAsync(new MessageFinalize { MsgNo = adminMsgNo, MsgTpCode = "CMD", ResTpCode = "REVIEW", MsgResult = "Admin reply did not resolve to APPROVE/REJECT." });
                Tracker.Track($"MsgNo {held.MsgNo}: admin reply did not resolve to APPROVE/REJECT, left on hold.");
                return;
            }

            // Reuse the candidate VendId directly on a bare confirmation (no correction given) -- only
            // re-resolve via lookup when the admin named something different, or there was no candidate at all.
            int? vendId = held.CandVendId;
            if (cmdResult.Value.VendorName is not null && !string.Equals(cmdResult.Value.VendorName, held.CandVendName, StringComparison.OrdinalIgnoreCase))
            {
                var matches = await mailDataSvc.LookupVendorAsync(cmdResult.Value.VendorName);
                if (matches.Count != 1)
                {
                    await mail.ReplyAsync(message.Id!, $"Vendor \"{cmdResult.Value.VendorName}\" not found. Please reply with the exact vendor name.");
                    await mailDataSvc.FinalizeMessageAsync(new MessageFinalize { MsgNo = adminMsgNo, MsgTpCode = "CMD", ResTpCode = "REVIEW", MsgResult = $"Vendor \"{cmdResult.Value.VendorName}\" not found (or ambiguous)." });
                    Tracker.Track($"MsgNo {held.MsgNo}: vendor \"{cmdResult.Value.VendorName}\" not found (or ambiguous), left on hold.");
                    return;
                }
                vendId = matches[0].VendId;
            }

            if (vendId is null)
            {
                await mail.ReplyAsync(message.Id!, "Please reply with the vendor name to link this sender to.");
                await mailDataSvc.FinalizeMessageAsync(new MessageFinalize { MsgNo = adminMsgNo, MsgTpCode = "CMD", ResTpCode = "REVIEW", MsgResult = "No vendor could be resolved." });
                Tracker.Track($"MsgNo {held.MsgNo}: no vendor could be resolved, left on hold.");
                return;
            }

            var isApproved = cmdResult.Value.CmdCode == "APPROVE";

            // Fetched before ResolveCommandAsync -- that call clears IsHeld on every match, so the pending
            // set has to be captured first or there'd be nothing left to reprocess/report on.
            var pending = await mailDataSvc.GetHeldMessagesAsync(held.SenderId, vendId.Value, held.MsgNo);

            if (isApproved)
            {
                var linkResult = await mailDataSvc.LinkVendorAsync(held.SenderId, account.AppId, vendId.Value);
                if (linkResult.IsFailure)
                {
                    await mail.ReplyAsync(message.Id!, "Something went wrong linking this vendor. Please try again or contact support.");
                    await mailDataSvc.FinalizeMessageAsync(new MessageFinalize { MsgNo = adminMsgNo, MsgTpCode = "CMD", ResTpCode = "FAILED", MsgResult = $"Failed to link vendor: {linkResult.Message}" });
                    Tracker.Track($"MsgNo {held.MsgNo}: failed to link VendId {vendId} to SenderId {held.SenderId} ({linkResult.Message}).");
                    return;
                }
            }

            await mailDataSvc.ResolveCommandAsync(new CommandResolve
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

            Tracker.Track($"{fromAddr}: {(isApproved ? "approved" : "rejected")} VendId {vendId} -- {pending.Count} message(s) affected.");

            if (isApproved)
                foreach (var heldMsg in pending)
                    await ReprocessHeldMessageAsync(account, mail, heldMsg, held.SenderId);

            await mail.ReplyAsync(message.Id!, isApproved
                ? $"Approved. {pending.Count} message(s) for this vendor were processed."
                : $"Rejected. {pending.Count} message(s) were declined.");

            await mailDataSvc.FinalizeMessageAsync(new MessageFinalize { MsgNo = adminMsgNo, MsgTpCode = "CMD", ResTpCode = "OK" });
        }

        // The specific gaps in a classification that should be flagged and drive PARTIAL vs HELD -- terse,
        // no MsgNo/sender/reference numbers (those are already columns on the same row). Vendor-unresolved
        // only counts as a gap when the sender is known (isKnownSender): for a first-contact sender, a null
        // VendId is the deliberate outcome of not opening the attachment yet, not something missing.
        private static List<string> GetMissingInfo(TriageResult result, bool isKnownSender)
        {
            var gaps = new List<string>();

            if (result.MsgTpCode is null)
                gaps.Add("message type could not be determined");
            else if (result.MsgTpCode == "SUB" && result.Attachments.Count == 0)
                gaps.Add("no attachments could be identified for processing");
            else if (result.MsgTpCode == "INQ" && result.InvcNbr is null)
                gaps.Add("no invoice number could be found for this inquiry");

            if (isKnownSender && result.VendId is null)
                gaps.Add("vendor could not be confirmed even after reviewing the attachment");

            return gaps;
        }

        // Builds the admin-facing comment for a hold/approval-request email -- echoes what was already
        // extracted (so approving isn't a blind "trust the sender" click) and calls out anything still
        // missing, so one reply can both decide and supply the gap instead of a second round-trip.
        // missingInfo is passed in rather than recomputed -- the caller already needed it for MsgResult.
        private static string GetApprovalComment(TriageResult result, bool isKnownSender, List<string> missingInfo)
        {
            var lines = new List<string>
            {
                result.VendName is not null
                    ? $"This sender is not yet linked to any vendor. It looks like it may be from \"{result.VendName}\" -- reply APPROVE to link it, REJECT to decline, or give the correct vendor name."
                    : "This sender is not yet linked to any vendor and none could be determined from the message. Reply with the vendor name to link it to, or REJECT to decline."
            };

            if (result.MsgTpCode == "SUB" && result.Attachments.Count > 0 && !isKnownSender)
                lines.Add("Attachments have not been opened yet -- I need to confirm this sender is allowed to submit invoices first. They'll be processed once approved.");

            lines.AddRange(missingInfo.Select(g => char.ToUpper(g[0]) + g[1..] + "."));

            return string.Join(" ", lines);
        }

        public async Task ProcessAccountAsync(MailAccount account)
        {
            var clientSecret = config[$"MailSecrets:{account.SecretName}"] ?? throw new InvalidOperationException($"Secret '{account.SecretName}' not found in configuration.");
            var mail = new GraphMailService(account, clientSecret);

            var (process, docTypes) = await filerDataSvc.GetProcessAndDocTypesAsync(account.AppId);

            // One call for the whole app (not one per doc type) -- GetDocTypeFieldsByAppAsync returns every
            // doc type's fields in one round trip, grouped here by DocTypeId. Cheap SQL reads either way,
            // but the per-doctype loop this replaced was an avoidable N+1 against dms.sprTblRouter.
            var allFields = await filerDataSvc.GetDocTypeFieldsByAppAsync(account.AppId);
            var fieldsByDocType = allFields
                .GroupBy(f => f.DocTypeId)
                .ToDictionary(g => g.Key, g => g.Select(f => new ClaudeFieldSpec
                {
                    FieldName = f.FieldName,
                    SqlDataType = f.SqlDataType,
                    MaxLength = f.MaxLength,
                    IsRequired = f.IsRequired,
                    Descr = f.Descr,
                    PosNo = f.PosNo,
                    FieldMode = f.FieldMode,
                    HndName = f.HndName,
                    Parameter = f.Parameter
                }).ToList());

            var (messages, deltaLink) = await mail.GetChangedMessagesAsync(account.LastMsgLink);
            Tracker.Track($"Account {account.AcctName}: {messages.Count} message(s) fetched.");

            foreach (var message in messages)
            {
                var fromAddr = message.From?.EmailAddress?.Address ?? "";

                try
                {
                    if (string.Equals(fromAddr, account.AdmAcctEMail, StringComparison.OrdinalIgnoreCase))
                    {
                        await ProcessAdminReplyAsync(account, mail, message);
                    }
                    else
                    {
                        // Checked before normal classification -- a reply to an open RFI takes this path
                        // instead of being triaged again as a fresh Submission/Inquiry.
                        var bridgeMsgIds = GraphMailService.GetBridgeMsgIds(message);
                        var rfiBridge = (await mailDataSvc.GetRfiBridgeAsync(bridgeMsgIds)).FirstOrDefault();

                        if (rfiBridge is not null)
                            await ProcessInfoRequestReplyAsync(account, mail, message, rfiBridge, docTypes, process?.ProcessDesc ?? "");
                        else
                            await ProcessInboundMessageAsync(account, mail, docTypes, fieldsByDocType, process?.ProcessDesc ?? "", message);
                    }
                }
                catch (Exception ex)
                {
                    Tracker.Track($"{fromAddr}: unhandled exception processing message ({ex.Message}).");
                }
            }

            var newWatermark = messages.Count > 0 ? messages.Max(m => m.ReceivedDateTime?.DateTime ?? DateTime.UtcNow) : DateTime.UtcNow;
            var pollResult = await mailDataSvc.SetLastPollAsync(new AccountPoll { AcctId = account.AcctId, LastPollDT = DateTime.UtcNow, LastMsgLink = deltaLink });
            if (pollResult.IsFailure)
                Tracker.Track($"Account {account.AcctName}: failed to update LastPollDT/LastMsgLink ({pollResult.Message}).");
        }
    }
}
