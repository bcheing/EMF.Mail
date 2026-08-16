using Cheing;
using Cheing.Net.Ai;
using EMF.DMS.Client;
using EMF.FilerSvc;
using EMF.FilerSvc.Models;
using EMF.Mail.Models;
using EMF.Mail.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Graph.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using DbBinder = Cheing.Binder;
using FilerDataService = EMF.FilerSvc.Services.DataService;
using GraphMessage = Microsoft.Graph.Models.Message;
using MailDataService = EMF.Mail.Services.DataService;
using NetBinder = Cheing.Net.Binder;

namespace EMF.Mail;

public static class Program
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    // args reserved for future CLI arguments (e.g. --account, --since).
    public static async Task Main(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json")
            .AddUserSecrets(Assembly.GetExecutingAssembly())
            .Build();

        var dbConfig = config.GetSection("DbConfig").Get<DbConfig>() ?? throw new InvalidOperationException("DbConfig section not found in appsettings.json.");

        var db = new DbBinder(dbConfig);
        var user = await db.GetObjAsync<AppUser>("/ou/user");
        db.UId = user.UId;

        var net = new NetBinder(dbConfig);
        var claude = new ClaudeService(net);
        var classifier = new ClaudeClassifier(claude, db);
        var triageSvc = new TriageService(claude, db, classifier);
        var cmdSvc = new CommandService(claude);
        var filerDataSvc = new FilerDataService(db);
        var dms = new PackageService(db);
        var filer = new Filer(filerDataSvc, classifier, dms);
        var mailDataSvc = new MailDataService(db);

        // Runs vendor identification + SUB/INQ classification for a message. Always runs, regardless of the
        // sender's linked-vendor count -- see TriageService for why. Returns the sender's linked-vendor history
        // alongside the classify result since callers need both (approval check + the fan-out-relevant list).
        async Task<(Result<TriageResult> Result, List<SenderHistory> History)> ClassifyMessageAsync(MailAccount account, List<DocType> docTypes, Dictionary<int, List<ClaudeFieldSpec>> fieldsByDocType, string processDesc, GraphMessage message, int senderId)
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

        // Acts on an already-classified message -- shared by the immediate path (classification just ran as
        // part of the hold decision, VendId matched a linked vendor) and the post-approval fan-out path
        // (classification is reused from the original hold's saved MsgContext, not re-run). saveContext is
        // false on the fan-out path -- MsgContext was already written when the message was first held, so
        // the finalize call there only needs to touch MsgTpId/ResTpId/IsProcessed, not resave it.
        async Task ProcessClassifiedMessageAsync(MailAccount account, GraphMailService mail, GraphMessage message, int msgNo, int senderId, TriageResult triageResult, bool saveContext)
        {
            var context = saveContext ? triageResult : null;

            if (triageResult.MsgTpCode is null)
            {
                await mail.MarkNeedsReviewAsync(message.Id!);
                await mail.ForwardAsync(message.Id!, account.AdmAcctEMail, "This message could not be classified and needs manual review.");
                await mailDataSvc.FinalizeMessageAsync(msgNo, context, null, null, null, "REVIEW", "Could not determine message type.");
                Tracker.Track($"MsgNo {msgNo}: could not classify message, flagged for review.");
                return;
            }

            if (triageResult.MsgTpCode == "INQ")
            {
                var matches = await mailDataSvc.GetReqStatusAsync(senderId, triageResult.InvcNbr ?? "");
                var reply = await triageSvc.ComposeInquiryReplyAsync(matches);

                if (reply.IsFailure)
                {
                    await mailDataSvc.FinalizeMessageAsync(msgNo, context, triageResult.MsgTpCode, null, null, "FAILED", $"Failed to compose inquiry reply: {reply.Message}");
                    Tracker.Track($"MsgNo {msgNo}: failed to compose inquiry reply ({reply.Message}).");
                    return;
                }

                await mail.ReplyAsync(message.Id!, reply.Value);
                await mail.FlagProcessedAsync(message.Id!);
                await mailDataSvc.FinalizeMessageAsync(msgNo, context, triageResult.MsgTpCode, null, null, "OK", $"Inquiry reply sent ({matches.Count} match(es) found).");
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

                    var attachResult = await filer.AttachDocumentAsync(result.Value.PkgNo, suppAttachment.ContentBytes, supporting.FileName);
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
                await mail.ReplyAsync(message.Id!, $"Your invoice(s) were processed. Reference number(s): {string.Join(", ", reqNos)}");
                await mailDataSvc.FinalizeMessageAsync(msgNo, context, triageResult.MsgTpCode, null, null, "OK", null);
            }
            else
            {
                await mailDataSvc.FinalizeMessageAsync(msgNo, context, triageResult.MsgTpCode, null, null, "NOOP", "No requests created from this message.");
                Tracker.Track($"MsgNo {msgNo}: no requests created from this message.");
            }
        }

        // Handles one inbound (non-admin) message: log it, classify it, then either hold for approval
        // or process it immediately depending on the sender's vendor link.
        async Task ProcessInboundMessageAsync(MailAccount account, GraphMailService mail, List<DocType> docTypes, Dictionary<int, List<ClaudeFieldSpec>> fieldsByDocType, string processDesc, GraphMessage message)
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
                    Tracker.Track($"{fromAddr}: already logged, skipping.");
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
                await mailDataSvc.FinalizeMessageAsync(msgNo, null, null, null, null, "REVIEW", $"Claude classification failed: {classifyResult.Message}");
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

                await mailDataSvc.FinalizeMessageAsync(msgNo, triageResult, triageResult.MsgTpCode, true, fwdMsgId, resTpCode, msgResultText);
                Tracker.Track(logMsg);
                return;
            }

            await ProcessClassifiedMessageAsync(account, mail, message, msgNo, senderId, triageResult, saveContext: true);
        }

        // Reprocesses one previously-held message after its vendor link is approved -- reuses the
        // classification saved in MsgContext at hold time instead of re-running Claude.
        async Task ReprocessHeldMessageAsync(MailAccount account, GraphMailService mail, HeldMessage heldMsg, int senderId)
        {
            var orig = await mail.GetMessageByIdAsync(heldMsg.MsgId);
            if (orig is null)
            {
                await mailDataSvc.FinalizeMessageAsync(heldMsg.MsgNo, null, null, null, null, "FAILED", "Approved but original message could not be refetched from Inbox.");
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
                await mailDataSvc.FinalizeMessageAsync(heldMsg.MsgNo, null, null, null, null, "REVIEW", "Saved MsgContext missing or unreadable on reprocess.");
                Tracker.Track($"MsgNo {heldMsg.MsgNo}: saved MsgContext missing or unreadable on reprocess.");
                return;
            }

            await ProcessClassifiedMessageAsync(account, mail, orig, heldMsg.MsgNo, senderId, triageResult, saveContext: false);
        }

        // Handles one admin reply: match it to a held message, interpret APPROVE/REJECT, act on it, and
        // fan out to every other message the sender has pending. Every admin message gets its own logged
        // row and a reply -- no path here leaves the admin without a response.
        async Task ProcessAdminReplyAsync(MailAccount account, GraphMailService mail, GraphMessage message)
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
                await mailDataSvc.FinalizeMessageAsync(adminMsgNo, null, "CMD", null, null, "REVIEW", "No matching hold found.");
                Tracker.Track($"Admin message from {fromAddr}: no matching hold.");
                return;
            }

            Tracker.Track($"Admin reply from {fromAddr} matched held MsgNo {held.MsgNo}.");

            var cmdResult = await cmdSvc.InterpretApprovalReplyAsync(message.Subject ?? "", message.UniqueBody?.Content ?? message.Body?.Content ?? "", held.CandVendName);

            if (cmdResult.IsFailure)
            {
                await mail.ReplyAsync(message.Id!, "Something went wrong interpreting that reply. Please try again.");
                await mailDataSvc.FinalizeMessageAsync(adminMsgNo, null, "CMD", null, null, "FAILED", $"Claude command interpretation failed: {cmdResult.Message}");
                Tracker.Track($"MsgNo {held.MsgNo}: Claude command interpretation failed ({cmdResult.Message}).");
                return;
            }

            // Explicit both ways -- anything that isn't exactly APPROVE or REJECT (including null) is
            // treated as "didn't understand," never silently folded into a REJECT.
            if (cmdResult.Value.CmdCode != "APPROVE" && cmdResult.Value.CmdCode != "REJECT")
            {
                await mail.ReplyAsync(message.Id!, "Sorry, I couldn't understand that reply. Please reply APPROVE, REJECT, or the correct vendor name.");
                await mailDataSvc.FinalizeMessageAsync(adminMsgNo, null, "CMD", null, null, "REVIEW", "Admin reply did not resolve to APPROVE/REJECT.");
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
                    await mailDataSvc.FinalizeMessageAsync(adminMsgNo, null, "CMD", null, null, "REVIEW", $"Vendor \"{cmdResult.Value.VendorName}\" not found (or ambiguous).");
                    Tracker.Track($"MsgNo {held.MsgNo}: vendor \"{cmdResult.Value.VendorName}\" not found (or ambiguous), left on hold.");
                    return;
                }
                vendId = matches[0].VendId;
            }

            if (vendId is null)
            {
                await mail.ReplyAsync(message.Id!, "Please reply with the vendor name to link this sender to.");
                await mailDataSvc.FinalizeMessageAsync(adminMsgNo, null, "CMD", null, null, "REVIEW", "No vendor could be resolved.");
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
                    await mailDataSvc.FinalizeMessageAsync(adminMsgNo, null, "CMD", null, null, "FAILED", $"Failed to link vendor: {linkResult.Message}");
                    Tracker.Track($"MsgNo {held.MsgNo}: failed to link VendId {vendId} to SenderId {held.SenderId} ({linkResult.Message}).");
                    return;
                }
            }

            await mailDataSvc.ResolveCommandAsync(held.SenderId, vendId.Value, held.MsgNo, isApproved, cmdResult.Value.CmdCode!, adminMsgNo, $"SenderId {held.SenderId}, VendId {vendId.Value}", 0, isApproved ? "Approved" : "Rejected");

            Tracker.Track($"{fromAddr}: {(isApproved ? "approved" : "rejected")} VendId {vendId} -- {pending.Count} message(s) affected.");

            if (isApproved)
                foreach (var heldMsg in pending)
                    await ReprocessHeldMessageAsync(account, mail, heldMsg, held.SenderId);

            await mail.ReplyAsync(message.Id!, isApproved
                ? $"Approved. {pending.Count} message(s) for this vendor were processed."
                : $"Rejected. {pending.Count} message(s) were declined.");

            await mailDataSvc.FinalizeMessageAsync(adminMsgNo, null, "CMD", null, null, "OK", null);
        }

        // The specific gaps in a classification that should be flagged and drive PARTIAL vs HELD -- terse,
        // no MsgNo/sender/reference numbers (those are already columns on the same row). Vendor-unresolved
        // only counts as a gap when the sender is known (isKnownSender): for a first-contact sender, a null
        // VendId is the deliberate outcome of not opening the attachment yet, not something missing.
        static List<string> GetMissingInfo(TriageResult result, bool isKnownSender)
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
        static string GetApprovalComment(TriageResult result, bool isKnownSender, List<string> missingInfo)
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

        async Task ProcessAccountAsync(MailAccount account)
        {
            var clientSecret = config[$"MailSecrets:{account.SecretName}"] ?? throw new InvalidOperationException($"Secret '{account.SecretName}' not found in configuration.");
            var mail = new GraphMailService(account, clientSecret);

            var (process, docTypes) = await filerDataSvc.GetProcessAndDocTypesAsync(account.AppId);

            // Fetched once per account, not per message -- these are plain SQL reads (cheap), reused by every
            // message's triage call this batch so a known sender's Submission can extract in the same Claude
            // call it already opened the document image for, without Filer reading the file again later.
            var fieldsByDocType = new Dictionary<int, List<ClaudeFieldSpec>>();
            foreach (var docType in docTypes)
            {
                var fields = await filerDataSvc.GetDocTypeFieldsAsync(account.AppId, docType.DocTpId);
                fieldsByDocType[docType.DocTpId] = fields.Select(f => new ClaudeFieldSpec
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
                }).ToList();
            }

            var messages = await mail.GetRecentMessagesAsync(account.LastPollDT);
            Tracker.Track($"Account {account.AcctName}: {messages.Count} message(s) fetched.");

            foreach (var message in messages)
            {
                var fromAddr = message.From?.EmailAddress?.Address ?? "";

                try
                {
                    if (string.Equals(fromAddr, account.AdmAcctEMail, StringComparison.OrdinalIgnoreCase))
                        await ProcessAdminReplyAsync(account, mail, message);
                    else
                        await ProcessInboundMessageAsync(account, mail, docTypes, fieldsByDocType, process?.ProcessDesc ?? "", message);
                }
                catch (Exception ex)
                {
                    Tracker.Track($"{fromAddr}: unhandled exception processing message ({ex.Message}).");
                }
            }

            var newWatermark = messages.Count > 0 ? messages.Max(m => m.ReceivedDateTime?.DateTime ?? DateTime.UtcNow) : DateTime.UtcNow;
            var pollResult = await mailDataSvc.SetLastPollAsync(account.AcctId, newWatermark);
            if (pollResult.IsFailure)
                Tracker.Track($"Account {account.AcctName}: failed to update LastPollDT ({pollResult.Message}).");
        }

        var accounts = await mailDataSvc.GetMailAccountsAsync();
        Tracker.Track($"Loaded {accounts.Count} mail account(s).");

        foreach (var account in accounts)
            await ProcessAccountAsync(account);
    }
}
