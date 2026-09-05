using Cheing;
using Cheing.Net.Ai;
using EMF.Mail.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FilerDataService = EMF.FilerSvc.Services.DataService;

namespace EMF.Mail.Services
{
    // Parallel to MessageProcessor, for message-type-driven apps (ai.LstMsgTypes/ai.LstMsgTypeFields)
    // instead of AP's flat vendor/SUB/INQ shape. No vendor gating, no RFI/admin-reply handling -- a
    // message classifies straight to zero or more instances, each saved via its message type's own
    // PutHndName.
    public class MsgTypeProcessor(DataService mailDataSvc, FilerDataService filerDataSvc, ClaudeClassifier classifier)
    {
        public async Task ProcessAccountAsync(MailAccount account, IMailService mail)
        {
            var (process, _) = await filerDataSvc.GetProcessAndDocTypesAsync(account.AppId);
            var msgTypes = await mailDataSvc.GetMsgTypesAsync(account.AppId);

            var fieldsByMsgType = new Dictionary<int, List<ClaudeFieldSpec>>();
            foreach (var type in msgTypes)
                fieldsByMsgType[type.MsgTpId] = await mailDataSvc.GetMsgTypeFieldsAsync(type.MsgTpId);

            var options = msgTypes.Select(t => new ClaudeClassification { Id = t.MsgTpId, Code = t.MsgTpCode, Name = t.MsgTpDesc, Desc = t.ClassifyHint ?? t.MsgTpDesc }).ToList();

            var (messages, deltaLink) = await mail.GetChangedMessagesAsync(account.LastMsgLink);
            Tracker.Track($"Account {account.AcctName}: {messages.Count} message(s) fetched.");

            foreach (var message in messages)
            {
                try
                {
                    await ProcessMessageAsync(account, mail, options, fieldsByMsgType, msgTypes, process?.ProcessDesc ?? "", message);
                }
                catch (Exception ex)
                {
                    Tracker.Track($"{message.FromAddr}: unhandled exception processing message ({ex.Message}).");
                }
            }

            var pollResult = await mailDataSvc.SetLastPollAsync(new AccountPoll { AcctId = account.AcctId, LastPollDT = DateTime.UtcNow, LastMsgLink = deltaLink });
            if (pollResult.IsFailure)
                Tracker.Track($"Account {account.AcctName}: failed to update LastPollDT/LastMsgLink ({pollResult.Message}).");
        }

        private async Task ProcessMessageAsync(MailAccount account, IMailService mail, List<ClaudeClassification> options, Dictionary<int, List<ClaudeFieldSpec>> fieldsByMsgType, List<MsgType> msgTypes, string processDesc, Message message)
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
            var content = new List<ClaudeContentPart> { new("text/plain", Text: $"From: {message.FromAddr}\nSubject: {message.Subject}\n\n{message.Body}") };

            var classifyResult = await classifier.ClassifyMsgTypeAsync(content, processDesc, options, fieldsByMsgType);

            if (classifyResult.IsFailure || classifyResult.Value.MsgTpCode is null)
            {
                await mail.MarkNeedsReviewAsync(message.ProvMsgId);
                await mailDataSvc.FinalizeMessageAsync(new MessageFinalize { MsgNo = msgNo, ResTpCode = "REVIEW", MsgResult = "Could not determine message type." });
                Tracker.Track($"MsgNo {msgNo}: could not classify message, flagged for review.");
                return;
            }

            var result = classifyResult.Value;
            var msgType = msgTypes.FirstOrDefault(t => t.MsgTpCode == result.MsgTpCode);

            if (msgType?.PutHndName is null)
            {
                await mailDataSvc.FinalizeMessageAsync(new MessageFinalize { MsgNo = msgNo, MsgContext = result, MsgTpCode = result.MsgTpCode, ResTpCode = "FAILED", MsgResult = $"No PutHndName configured for message type {result.MsgTpCode}." });
                Tracker.Track($"MsgNo {msgNo}: no PutHndName configured for {result.MsgTpCode}.");
                return;
            }

            var saved = 0;
            foreach (var instance in result.Instances)
            {
                var putResult = await mailDataSvc.PutMsgTypeInstanceAsync(msgType.PutHndName, instance);
                if (putResult.IsFailure)
                    Tracker.Track($"MsgNo {msgNo}: failed to save {result.MsgTpCode} instance ({putResult.Message}).");
                else
                    saved++;
            }

            await mail.FlagProcessedAsync(message.ProvMsgId);
            await mailDataSvc.FinalizeMessageAsync(new MessageFinalize { MsgNo = msgNo, MsgContext = result, MsgTpCode = result.MsgTpCode, ResTpCode = saved == result.Instances.Count ? "OK" : "PARTIAL", MsgResult = $"{saved} of {result.Instances.Count} record(s) saved." });
            Tracker.Track($"MsgNo {msgNo}: {saved} of {result.Instances.Count} {result.MsgTpCode} record(s) saved.");
        }
    }
}