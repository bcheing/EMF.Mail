using Cheing;
using Cheing.Net.Ai;
using EMF.DMS.Client;
using EMF.FilerSvc;
using EMF.Mail.Models;
using EMF.Mail.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Graph.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using DbBinder = Cheing.Binder;
using NetBinder = Cheing.Net.Binder;
using FilerDataService = EMF.FilerSvc.Services.DataService;
using MailDataService = EMF.MailSvc.Services.DataService;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json")
    .Build();

var dbConfig = config.GetSection("DbConfig").Get<DbConfig>() ?? throw new InvalidOperationException("DbConfig section not found in appsettings.json.");

var db = new DbBinder(dbConfig);
var user = await db.GetObjAsync<AppUser>("/ou/user");
db.UId = user.UId;

var net = new NetBinder(dbConfig);
var claude = new ClaudeService(net);
var classifier = new ClaudeClassifier(claude, db);
var triage = new TriageService(claude);
var filerData = new FilerDataService(db);
var dms = new PackageService(db);
var filer = new Filer(filerData, classifier, dms);
var runnerData = new MailDataService(db);

var accounts = await runnerData.GetMailAccountsAsync();

foreach (var account in accounts)
{
    var clientSecret = config[$"MailSecrets:{account.SecretName}"] ?? throw new InvalidOperationException($"Secret '{account.SecretName}' not found in configuration.");
    var mail = new GraphMailService(account, clientSecret);

    var (process, docTypes) = await filerData.GetProcessAndDocTypesAsync(account.AppId);

    foreach (var message in await mail.GetRecentMessagesAsync())
    {
        var msgId = message.InternetMessageId ?? message.Id!;

        var msgResult = await runnerData.SaveMessageAsync(new MailMessage
        {
            MsgId = msgId,
            FromAddr = message.From?.EmailAddress?.Address ?? "",
            FromName = message.From?.EmailAddress?.Name ?? "",
            RcptDate = message.ReceivedDateTime?.DateTime ?? DateTime.UtcNow,
            Subject = message.Subject ?? "",
            OrigMsgId = GraphMailService.GetOrigMsgId(message)
        });

        if (msgResult.IsFailure)
        {
            if (msgResult.Code != "2627")
                Tracker.Track($"MsgId {msgId}: failed to log message ({msgResult.Message}).");
            continue;
        }

        var senderId = msgResult.Value.SenderId;
        var history = await runnerData.GetSenderHistoryAsync(senderId);
        var fileNames = message.Attachments?.OfType<FileAttachment>().Select(a => a.Name ?? "").ToList() ?? [];
        var body = message.Body?.Content ?? "";

        var triageResult = await triage.ClassifyAsync(message.Subject ?? "", body, message.From?.EmailAddress?.Address ?? "", fileNames, docTypes, history);

        if (triageResult.IsFailure || triageResult.Value.MsgTpCode is null)
        {
            Tracker.Track($"MsgNo {msgResult.Value.MsgNo}: could not classify message, flagged for review.");
            await mail.MarkNeedsReviewAsync(message.Id!);
            await mail.ForwardAsync(message.Id!, account.AdmAcctName, "This message could not be classified and needs manual review.");
            continue;
        }

        await runnerData.SaveMsgTypeAsync(msgResult.Value.MsgNo, triageResult.Value.MsgTpCode);

        if (triageResult.Value.MsgTpCode == "INQ")
        {
            var matches = await runnerData.GetReqStatusAsync(senderId, triageResult.Value.InvcNbr ?? "");
            var reply = await triage.ComposeInquiryReplyAsync(matches);

            if (reply.IsFailure)
            {
                Tracker.Track($"MsgNo {msgResult.Value.MsgNo}: failed to compose inquiry reply ({reply.Message}).");
                continue;
            }

            await mail.ReplyAsync(message.Id!, reply.Value);
            await mail.FlagProcessedAsync(message.Id!);
            continue;
        }

        var items = new List<MessageItem>();
        var reqNos = new List<int>();

        foreach (var group in triageResult.Value.Attachments.GroupBy(a => a.GroupId))
        {
            var processing = group.FirstOrDefault(a => a.Label == "Processing");
            if (processing is null)
            {
                Tracker.Track($"MsgNo {msgResult.Value.MsgNo}: group {group.Key} has no Processing attachment, skipping.");
                continue;
            }

            var attachment = message.Attachments?.OfType<Microsoft.Graph.Models.FileAttachment>().FirstOrDefault(a => a.Name == processing.FileName);
            if (attachment?.ContentBytes is null)
            {
                Tracker.Track($"MsgNo {msgResult.Value.MsgNo}: attachment {processing.FileName} not found or empty, skipping.");
                continue;
            }

            var mediaType = Filer.GetMediaType(processing.FileName);
            var result = await filer.ProcessDocumentAsync(account.AppId, account.OwnerUId, attachment.ContentBytes, mediaType, processing.FileName);
            if (result.IsFailure)
            {
                Tracker.Track($"MsgNo {msgResult.Value.MsgNo}: {result.Message}");
                continue;
            }

            items.Add(new MessageItem { MsgNo = msgResult.Value.MsgNo, PkgNo = result.Value.PkgNo, DocNo = result.Value.DocNo });
            reqNos.Add(result.Value.ReqNo);

            foreach (var supporting in group.Where(a => a.Label == "Supporting"))
            {
                var suppAttachment = message.Attachments?.OfType<Microsoft.Graph.Models.FileAttachment>().FirstOrDefault(a => a.Name == supporting.FileName);
                if (suppAttachment?.ContentBytes is null)
                {
                    Tracker.Track($"MsgNo {msgResult.Value.MsgNo}: supporting attachment {supporting.FileName} not found or empty, skipping.");
                    continue;
                }

                var attachResult = await filer.AttachDocumentAsync(result.Value.PkgNo, suppAttachment.ContentBytes, supporting.FileName);
                if (attachResult.IsFailure)
                {
                    Tracker.Track($"MsgNo {msgResult.Value.MsgNo}: {attachResult.Message}");
                    continue;
                }

                items.Add(new MessageItem { MsgNo = msgResult.Value.MsgNo, PkgNo = result.Value.PkgNo, DocNo = attachResult.Value });
            }
        }

        if (items.Count > 0)
            await runnerData.SaveMessageItemsAsync(items);

        if (reqNos.Count > 0)
        {
            await mail.FlagProcessedAsync(message.Id!);
            await mail.ReplyAsync(message.Id!, $"Your invoice(s) were processed. Reference number(s): {string.Join(", ", reqNos)}");
        }
    }
}
