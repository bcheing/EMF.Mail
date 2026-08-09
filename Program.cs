using Cheing;
using Cheing.Net.Ai;
using EMF.FilerSvc;
using EMF.Mail.Models;
using EMF.Mail.Services;
using Microsoft.Extensions.Configuration;
using System;
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
var user = await db.GetObjAsync<User>("/ou/user");
db.UId = user.UId;

var net = new NetBinder(dbConfig);
var claude = new ClaudeService(net);
var classifier = new ClaudeClassifier(claude, db);
var filerData = new FilerDataService(db);
var filer = new Filer(filerData, classifier);
var runnerData = new MailDataService(db);

var accounts = await db.GetTListAsync<MailAccount>("/dms/apps/accounts");

foreach (var account in accounts)
{
    var clientSecret = config[$"MailSecrets:{account.SecretName}"] ?? throw new InvalidOperationException($"Secret '{account.SecretName}' not found in configuration.");
    var mail = new GraphMailService(account, clientSecret);

    foreach (var message in await mail.GetRecentMessagesAsync())
    {
        var msgId = message.InternetMessageId ?? message.Id!;

        var msgResult = await runnerData.SaveMessageAsync(new Message
        {
            MsgId = msgId,
            FromAddr = message.From?.EmailAddress?.Address ?? "",
            FromName = message.From?.EmailAddress?.Name ?? "",
            RcptDate = message.ReceivedDateTime?.DateTime ?? DateTime.UtcNow,
            Subject = message.Subject ?? ""
        });

        if (msgResult.IsFailure)
        {
            if (msgResult.Code != "2627")
                Tracker.Track($"MsgId {msgId}: failed to log message ({msgResult.Message}).");
            continue;
        }

        var attachment = message.Attachments?.OfType<Microsoft.Graph.Models.FileAttachment>().FirstOrDefault();
        if (attachment?.ContentBytes is null)
        {
            Tracker.Track($"MsgNo {msgResult.Value.MsgNo}: no file attachment found, skipping.");
            continue;
        }

        var mediaType = Filer.GetMediaType(attachment.Name ?? "");
        var result = await filer.ProcessDocumentAsync(account.AppId, attachment.ContentBytes, mediaType, attachment.Name ?? "attachment");
        if (result.IsFailure)
        {
            Tracker.Track($"MsgNo {msgResult.Value.MsgNo}: {result.Message}");
            continue;
        }

        await runnerData.SaveMessageItemAsync(new MessageItem { MsgNo = msgResult.Value.MsgNo, PkgNo = result.Value.PkgNo });

        await mail.FlagProcessedAsync(message.Id!);
        await mail.ReplyAsync(message.Id!, $"Your invoice was processed. Reference number: {result.Value.ReqNo}");
    }
}
