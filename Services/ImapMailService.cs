using Cheing;
using EMF.FilerSvc;
using EMF.Mail.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace EMF.Mail.Services
{
    public class ImapMailService(ImapAccount account, string password) : IMailService
    {
        private readonly ImapAccount _account = account;
        private readonly string _password = password;

        #region Public methods

        // No @odata.deltaLink equivalent exists for IMAP -- UIDVALIDITY+UID is the closest analog, encoded
        // into the same opaque LastMsgLink string Graph's delta link already uses, so no schema change was
        // needed for this. Bootstrap (lastMsgLink null) mirrors Graph's: capture the current high-water
        // mark and return zero messages, rather than walking the mailbox's full history.
        public async Task<(List<InternetMessage> Messages, string DeltaLink)> GetChangedMessagesAsync(string? lastMsgLink)
        {
            using var client = await GetClientAsync();
            var inbox = await GetInboxAsync(client, FolderAccess.ReadOnly);

            var uidValidity = inbox.UidValidity;
            var (storedValidity, lastUid) = GetLastBookmark(lastMsgLink);

            // Bootstrap, or the mailbox was recreated (UIDVALIDITY changed) -- either way, don't walk
            // history; capture the current high-water mark and start fresh from here. A UIDVALIDITY change
            // mid-operation is unusual enough to be worth a system-level notification, not just a log line.
            if (lastMsgLink is null || storedValidity != uidValidity)
            {
                if (lastMsgLink is not null)
                    Tracker.Notify($"IMAP account {_account.AcctName}: UIDVALIDITY changed ({storedValidity} -> {uidValidity}), resetting watermark to current -- messages received during this gap will not be fetched.");

                var nextUid = inbox.UidNext ?? new UniqueId(uidValidity, 1);
                return ([], $"{uidValidity}:{(nextUid.Id > 0 ? nextUid.Id - 1 : 0)}");
            }

            var uids = await inbox.SearchAsync(SearchQuery.Uids(new UniqueIdRange(new UniqueId(uidValidity, lastUid + 1), UniqueId.MaxValue)));

            var messages = new List<InternetMessage>();
            foreach (var uid in uids)
            {
                var mime = await inbox.GetMessageAsync(uid);
                messages.Add(MimeMapper.GetInternetMessage(mime, $"{uidValidity}:{uid.Id}"));
            }

            var newLastUid = uids.Count > 0 ? uids.Max(u => u.Id) : lastUid;
            return (messages, $"{uidValidity}:{newLastUid}");
        }

        // Used to reprocess an originally-held message after admin approval. IMAP's HEADER search key is
        // a substring match (RFC 3501), so searching the exact stored Message-Id (brackets included)
        // reliably finds it.
        public async Task<InternetMessage?> GetMessageByIdAsync(string internetMessageId)
        {
            using var client = await GetClientAsync();
            var inbox = await GetInboxAsync(client, FolderAccess.ReadOnly);

            var uids = await inbox.SearchAsync(SearchQuery.HeaderContains("Message-Id", internetMessageId));
            if (uids.Count == 0) return null;

            var mime = await inbox.GetMessageAsync(uids[0]);
            return MimeMapper.GetInternetMessage(mime, $"{inbox.UidValidity}:{uids[0].Id}");
        }
        public async Task ReplyAsync(InternetMessage message, string comment)
        {
            var orig = MimeMapper.GetMimeMessage(message.MimeContent);

            var reply = new MimeMessage();
            reply.From.Add(MailboxAddress.Parse(_account.AcctName));
            reply.To.Add(orig.From.Mailboxes.First());
            reply.Subject = orig.Subject is not null && orig.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? orig.Subject : $"Re: {orig.Subject}";
            reply.MessageId = MimeUtils.GenerateMessageId();
            SetThreadingHeaders(reply, orig);
            reply.Body = new TextPart("plain") { Text = comment };

            await SendAsync(reply);
        }
        public async Task ForwardAsync(InternetMessage message, string toAddr, string comment)
        {
            var orig = MimeMapper.GetMimeMessage(message.MimeContent);

            await SendAsync(GetBuiltMessage(_account, orig, toAddr, comment));
        }
        // Message-Id is generated here (not server-assigned post-send the way Graph's createForward is) --
        // set via MimeMessage.MessageId (bare id) and read back via the raw header (bracketed), so the
        // returned value matches the bracketed format everything else in this pipeline compares against.
        public async Task<string?> SendApprovalRequestAsync(InternetMessage message, string toAddr, string comment)
        {
            var orig = MimeMapper.GetMimeMessage(message.MimeContent);

            var forward = GetBuiltMessage(_account, orig, toAddr, comment);
            await SendAsync(forward);

            return forward.Headers[HeaderId.MessageId];
        }
        public async Task<string?> SendInfoRequestAsync(InternetMessage message, string comment)
        {
            var orig = MimeMapper.GetMimeMessage(message.MimeContent);

            var reply = new MimeMessage();
            reply.From.Add(MailboxAddress.Parse(_account.AcctName));
            reply.To.Add(orig.From.Mailboxes.First());
            reply.Subject = orig.Subject is not null && orig.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? orig.Subject : $"Re: {orig.Subject}";
            reply.MessageId = MimeUtils.GenerateMessageId();
            SetThreadingHeaders(reply, orig);
            reply.Body = new TextPart("plain") { Text = comment };

            await SendAsync(reply);

            return reply.Headers[HeaderId.MessageId];
        }

        #endregion

        #region Private methods

        private static (uint UidValidity, uint LastUid) GetLastBookmark(string? lastMsgLink)
        {
            if (lastMsgLink is null) return (0, 0);
            var parts = lastMsgLink.Split(':');
            return (uint.Parse(parts[0]), uint.Parse(parts[1]));
        }
        private async Task<ImapClient> GetClientAsync()
        {
            var client = new ImapClient();
            await client.ConnectAsync(_account.ImapHost, _account.ImapPort, SecureSocketOptions.Auto);
            await client.AuthenticateAsync(_account.AcctName, _password);
            return client;
        }
        private static async Task<IMailFolder> GetInboxAsync(ImapClient client, FolderAccess access)
        {
            var inbox = client.Inbox ?? throw new InvalidOperationException("IMAP server returned no Inbox folder.");
            await inbox.OpenAsync(access);
            return inbox;
        }
        // Graph's /forward embeds the full original message automatically; MailKit has no equivalent
        // helper, so the original is attached here as a real .eml (message/rfc822) part -- simple, and
        // (unlike Reply's inline quoting) worth doing regardless of "start simple": an approval-hold
        // forward with nothing attached to review would defeat its own purpose.
        private static MimeMessage GetBuiltMessage(ImapAccount account, MimeMessage orig, string toAddr, string comment)
        {
            var msg = new MimeMessage();
            msg.From.Add(MailboxAddress.Parse(account.AcctName));
            msg.To.Add(MailboxAddress.Parse(toAddr));
            msg.Subject = orig.Subject is not null && orig.Subject.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase) ? orig.Subject : $"Fwd: {orig.Subject}";
            msg.MessageId = MimeUtils.GenerateMessageId();
            msg.Body = new Multipart("mixed")
            {
                new TextPart("plain") { Text = comment },
                new MessagePart("rfc822") { Message = orig }
            };
            return msg;
        }
        private static void SetThreadingHeaders(MimeMessage outgoing, MimeMessage orig)
        {
            var origMessageId = orig.Headers[HeaderId.MessageId];
            if (origMessageId is null) return;

            outgoing.Headers[HeaderId.InReplyTo] = origMessageId;

            var origReferences = orig.Headers[HeaderId.References];
            outgoing.Headers[HeaderId.References] = origReferences is not null ? $"{origReferences} {origMessageId}" : origMessageId;
        }
        private async Task SendAsync(MimeMessage message)
        {
            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_account.SmtpHost, _account.SmtpPort, SecureSocketOptions.Auto);
            await smtp.AuthenticateAsync(_account.AcctName, _password);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);
        }

        #endregion
    }
}
