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
using System.Threading.Tasks;
using DbBinder = Cheing.Binder;
using FilerDataService = EMF.FilerSvc.Services.DataService;
using GraphMessage = Microsoft.Graph.Models.Message;
using MailDataService = EMF.Mail.Services.DataService;
using NetBinder = Cheing.Net.Binder;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json")
    .AddUserSecrets(System.Reflection.Assembly.GetExecutingAssembly())
    .Build();

var dbConfig = config.GetSection("DbConfig").Get<DbConfig>() ?? throw new InvalidOperationException("DbConfig section not found in appsettings.json.");

var db = new DbBinder(dbConfig);
var user = await db.GetObjAsync<AppUser>("/ou/user");
db.UId = user.UId;

var net = new NetBinder(dbConfig);
var claude = new ClaudeService(net);
var classifier = new ClaudeClassifier(claude, db);
var triageSvc = new TriageService(claude);
var cmdSvc = new CommandService(claude);
var filerDataSvc = new FilerDataService(db);
var dms = new PackageService(db);
var filer = new Filer(filerDataSvc, classifier, dms);
var mailDataSvc = new MailDataService(db);

// Classify + dispatch for a message from an already-approved sender. Shared by the live per-message loop
// and by the post-approval resume path, so a held sender's original submission gets exactly the same
// treatment a normally-approved sender's mail would have gotten -- no admin-instruction-as-context, no
// special handling if this itself comes back undetermined (falls through to NeedsReview same as any other).
async Task ProcessApprovedMessageAsync(MailAccount account, AppProcess? process, List<DocType> docTypes, GraphMailService mail, GraphMessage message, int msgNo, int senderId)
{
    var history = await mailDataSvc.GetSenderHistoryAsync(account.GetSenderHistHndName, senderId);
    var fileNames = message.Attachments?.OfType<FileAttachment>().Select(a => a.Name ?? "").ToList() ?? [];
    var body = message.Body?.Content ?? "";
    var fromAddr = message.From?.EmailAddress?.Address ?? "";

    var triageResult = await triageSvc.ClassifyAsync(message.Subject ?? "", body, fromAddr, fileNames, docTypes, history);

    if (triageResult.IsFailure)
    {
        Tracker.Track($"MsgNo {msgNo}: Claude classification failed ({triageResult.Message}).");
        await mail.MarkNeedsReviewAsync(message.Id!);
        await mail.ForwardAsync(message.Id!, account.AdmAcctEMail, "This message could not be classified and needs manual review.");
        return;
    }

    if (triageResult.Value.MsgTpCode is null)
    {
        Tracker.Track($"MsgNo {msgNo}: could not classify message, flagged for review.");
        await mail.MarkNeedsReviewAsync(message.Id!);
        await mail.ForwardAsync(message.Id!, account.AdmAcctEMail, "This message could not be classified and needs manual review.");
        return;
    }

    await mailDataSvc.SaveMsgTypeAsync(msgNo, triageResult.Value.MsgTpCode);
    Tracker.Track($"MsgNo {msgNo}: classified as {triageResult.Value.MsgTpCode}.");

    if (triageResult.Value.MsgTpCode == "INQ")
    {
        var matches = await mailDataSvc.GetReqStatusAsync(senderId, triageResult.Value.InvcNbr ?? "");
        var reply = await triageSvc.ComposeInquiryReplyAsync(matches);

        if (reply.IsFailure)
        {
            Tracker.Track($"MsgNo {msgNo}: failed to compose inquiry reply ({reply.Message}).");
            return;
        }

        await mail.ReplyAsync(message.Id!, reply.Value);
        await mail.FlagProcessedAsync(message.Id!);
        Tracker.Track($"MsgNo {msgNo}: inquiry reply sent ({matches.Count} match(es) found).");
        return;
    }

    var items = new List<MessageItem>();
    var reqNos = new List<int>();

    foreach (var group in triageResult.Value.Attachments.GroupBy(a => a.GroupId))
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
        var result = await filer.ProcessDocumentAsync(account.AppId, account.OwnerUId, attachment.ContentBytes, mediaType, processing.FileName);
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
    }
    else
    {
        Tracker.Track($"MsgNo {msgNo}: no requests created from this message.");
    }
}

var accounts = await mailDataSvc.GetMailAccountsAsync();
Tracker.Track($"Loaded {accounts.Count} mail account(s).");

foreach (var account in accounts)
{
    var clientSecret = config[$"MailSecrets:{account.SecretName}"] ?? throw new InvalidOperationException($"Secret '{account.SecretName}' not found in configuration.");
    var mail = new GraphMailService(account, clientSecret);

    var (process, docTypes) = await filerDataSvc.GetProcessAndDocTypesAsync(account.AppId);
    var messages = await mail.GetRecentMessagesAsync(account.LastPollDT);
    Tracker.Track($"Account {account.AcctName}: {messages.Count} message(s) fetched.");

    foreach (var message in messages)
    {
        var fromAddr = message.From?.EmailAddress?.Address ?? "";
        var msgId = message.InternetMessageId ?? message.Id!;

        if (string.Equals(fromAddr, account.AdmAcctEMail, StringComparison.OrdinalIgnoreCase))
        {
            var bridgeMsgIds = GraphMailService.GetBridgeMsgIds(message);
            HeldMessage? held = null;
            foreach (var candidate in bridgeMsgIds)
            {
                held = (await mailDataSvc.GetHeldMessageAsync(candidate)).FirstOrDefault();
                if (held is not null) break;
            }

            if (held is null)
            {
                Tracker.Track($"Admin message from {fromAddr}: no matching hold, command dispatch not yet implemented.");
                continue;
            }

            Tracker.Track($"Admin reply from {fromAddr} matched held MsgNo {held.MsgNo}.");

            var cmdResult = await cmdSvc.InterpretApprovalReplyAsync(message.Subject ?? "", message.UniqueBody?.Content ?? message.Body?.Content ?? "");

            if (cmdResult.IsFailure)
            {
                Tracker.Track($"MsgNo {held.MsgNo}: Claude command interpretation failed ({cmdResult.Message}).");
                continue;
            }

            if (cmdResult.Value.CmdCode is null)
            {
                Tracker.Track($"MsgNo {held.MsgNo}: admin reply did not resolve to APPROVE/REJECT, left on hold.");
                continue;
            }

            var isApproved = cmdResult.Value.CmdCode == "APPROVE";

            // Fetched before ResolveHoldAsync -- that call clears IsHeld on every match, so the pending
            // set has to be captured first or there'd be nothing left to reprocess/report on.
            var pending = await mailDataSvc.GetHeldMessagesAsync(held.SenderId);
            await mailDataSvc.ReleaseHoldAsync(held.SenderId, isApproved);

            Tracker.Track($"{fromAddr}: {(isApproved ? "approved" : "rejected")} -- {pending.Count} message(s) affected.");

            if (isApproved)
            {
                foreach (var heldMsg in pending)
                {
                    var orig = await mail.GetMessageByIdAsync(heldMsg.MsgId);
                    if (orig is null)
                    {
                        Tracker.Track($"MsgNo {heldMsg.MsgNo}: approved but original message could not be refetched from Inbox.");
                        continue;
                    }

                    await ProcessApprovedMessageAsync(account, process, docTypes, mail, orig, heldMsg.MsgNo, held.SenderId);
                }
            }

            continue;
        }

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
            continue;
        }

        Tracker.Track($"MsgNo {msgResult.Value.MsgNo} from {fromAddr}: IsApproved {msgResult.Value.IsApproved}.");

        if (!msgResult.Value.IsApproved)
        {
            // A sender's second (or Nth) message while still unresolved gets held silently -- only the
            // first pending message for a sender triggers the actual forward to admin.
            var pending = await mailDataSvc.GetHeldMessagesAsync(msgResult.Value.SenderId);

            string? fwdMsgId = null;
            if (pending.Count == 0)
                fwdMsgId = await mail.SendApprovalRequestAsync(message.Id!, account.AdmAcctEMail);

            var holdResult = await mailDataSvc.SetHoldAsync(msgResult.Value.MsgNo, true, fwdMsgId);
            if (holdResult.IsFailure)
                Tracker.Track($"MsgNo {msgResult.Value.MsgNo}: failed to set hold ({holdResult.Message}).");

            Tracker.Track(pending.Count == 0
                ? $"MsgNo {msgResult.Value.MsgNo} ({fromAddr}): unapproved sender, held pending admin approval."
                : $"MsgNo {msgResult.Value.MsgNo} ({fromAddr}): unapproved sender, already has a pending request, held silently.");

            continue;
        }

        await ProcessApprovedMessageAsync(account, process, docTypes, mail, message, msgResult.Value.MsgNo, msgResult.Value.SenderId);
    }

    var newWatermark = messages.Count > 0 ? messages.Max(m => m.ReceivedDateTime?.DateTime ?? DateTime.UtcNow) : DateTime.UtcNow;
    await mailDataSvc.SetLastPollAsync(account.AcctId, newWatermark);
}
