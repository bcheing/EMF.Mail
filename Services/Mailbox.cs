using Cheing;
using EMF.Mail.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace EMF.Mail.Services
{
    // ai: The only path that reads new mail. Saves each message (indexing data, raw content, NEW event) under a job and moves the bookmark; DMS and MailRunner both call it, and the job lock keeps them from running at the same time.
    public class Mailbox(DataService mailDataSvc)
    {
        private readonly DataService _mailDataSvc = mailDataSvc;

        public async Task<Result<int>> DownloadMessagesAsync(Account account, IMailService mail)
        {
            var jobResult = await _mailDataSvc.StartJobAsync(account.AcctId);
            if (jobResult.IsFailure) return Result.Fail<int>(jobResult.Message);
            if (jobResult.Value.JobNo is not int jobNo) return Result.Ok(0);

            var numMessages = 0;
            string? lastMsgLink = null;
            try
            {
                var bookmark = await _mailDataSvc.GetAccountBookmarkAsync(account.AcctId);
                var (messages, deltaLink) = await mail.GetChangedMessagesAsync(bookmark?.LastMsgLink);
                Tracker.Track($"Account {account.AcctName}: {messages.Count} message(s) downloaded.");

                var result = await SaveMessagesAsync(account, jobNo, messages);
                if (!result.IsFailure)
                {
                    numMessages = result.Value;
                    lastMsgLink = deltaLink;
                }
                return result;
            }
            finally
            {
                var jobEndResult = await _mailDataSvc.EndJobAsync(jobNo, numMessages, lastMsgLink);
                if (jobEndResult.IsFailure)
                    Tracker.Track($"Account {account.AcctName}: failed to end job {jobNo} and save the bookmark ({jobEndResult.Message}).");
            }
        }

        // ai: Stored content first; only when none is stored (pruned or never saved) the server is asked, and what it returns is stored again (the funnel). A message the server no longer has goes to MISSING, so it is asked once.
        public async Task<InternetMessage?> GetStoredMessageAsync(int msgNo, IMailService mail)
        {
            var content = await _mailDataSvc.GetMessageContentAsync(msgNo);
            if (content is null) return null;
            if (content.MsgContent is byte[] mimeContent) return MimeMapper.GetInternetMessage(mimeContent, content.ProvMsgId ?? content.MsgId);
            if (content.QCode == "MISSING") return null;

            var message = await mail.GetMessageByIdAsync(content.MsgId);
            if (message is null)
            {
                var missingResult = await _mailDataSvc.SaveMessageFinalizationAsync(new MessageFinalization { MsgNo = msgNo, ResTpCode = "MISSING", MsgResult = "Message not stored and no longer on the mail server." });
                if (missingResult.IsFailure)
                    Tracker.Track($"MsgNo {msgNo}: not on the mail server and could not be marked MISSING ({missingResult.Message}).");
                return null;
            }

            var saveResult = await _mailDataSvc.SaveMessageContentAsync(msgNo, Convert.ToBase64String(message.MimeContent));
            if (saveResult.IsFailure)
                Tracker.Track($"MsgNo {msgNo}: re-fetched from the mail server but could not be stored again ({saveResult.Message}).");

            return message;
        }

        private async Task<Result<int>> SaveMessagesAsync(Account account, int jobNo, List<InternetMessage> messages)
        {
            var numMessages = 0;
            foreach (var message in messages)
            {
                var msgResult = await _mailDataSvc.SaveMessageAsync(GetMessage(account.AcctId, jobNo, message));

                if (msgResult.IsFailure && msgResult.Code != "2627")
                {
                    Tracker.Notify($"Account {account.AcctName}: {message.FromAddr}: failed to log message ({msgResult.Message}). Download stopped, bookmark not saved.");
                    return Result.Fail<int>(msgResult.Message);
                }

                if (!msgResult.IsFailure)
                    numMessages++;
            }

            return Result.Ok(numMessages);
        }
        private static Message GetMessage(int acctId, int jobNo, InternetMessage message) => new()
        {
            AcctId = acctId,
            JobNo = jobNo,
            MsgId = message.InternetMessageId ?? message.ProvMsgId,
            ProvMsgId = message.ProvMsgId,
            FromAddr = message.FromAddr,
            FromName = message.FromName,
            RcptDT = message.ReceivedDateTime,
            Subject = message.Subject,
            OrigMsgId = message.OrigMsgId,
            NumAttachments = (byte)message.Attachments.Count,
            MsgContent = Convert.ToBase64String(message.MimeContent)
        };
    }
}
