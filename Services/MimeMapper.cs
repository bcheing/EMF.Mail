using EMF.FilerSvc;
using EMF.Mail.Models;
using MimeKit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EMF.Mail.Services
{
    // ai: One mapping from a raw message (MIME) to InternetMessage, used by the IMAP provider and by every step that reads a stored message.
    public static class MimeMapper
    {
        public static InternetMessage GetInternetMessage(byte[] mimeContent, string provMsgId) => GetInternetMessage(GetMimeMessage(mimeContent), provMsgId);
        public static MimeMessage GetMimeMessage(byte[] mimeContent)
        {
            using var ms = new MemoryStream(mimeContent);
            return MimeMessage.Load(ms);
        }
        public static InternetMessage GetInternetMessage(MimeMessage mime, string provMsgId) => new()
        {
            ProvMsgId = provMsgId,
            InternetMessageId = mime.Headers[HeaderId.MessageId],
            FromAddr = mime.From.Mailboxes.FirstOrDefault()?.Address ?? "",
            FromName = mime.From.Mailboxes.FirstOrDefault()?.Name ?? "",
            ReceivedDateTime = mime.Date.UtcDateTime,
            Subject = mime.Subject ?? "",
            Body = mime.HtmlBody ?? mime.TextBody ?? "",
            // No IMAP/MimeKit equivalent of Graph's server-stripped uniqueBody exists -- full body reused
            // here as a deliberate, noted simplification (see ProjectContext).
            UniqueBody = mime.HtmlBody ?? mime.TextBody ?? "",
            Attachments = mime.Attachments.OfType<MimePart>()
                .Select(p => (Part: p, Bytes: GetBinaryContent(p)))
                .Where(x => x.Bytes.Length > 0)
                .Select(x => new InternetMessageAttachment(x.Part.FileName ?? "attachment", x.Bytes, Filer.GetMediaType(x.Part.FileName ?? "")))
                .ToList(),
            OrigMsgId = mime.Headers[HeaderId.InReplyTo],
            BridgeMsgIds = GetBridgeMsgIds(mime),
            MimeContent = GetMimeContent(mime)
        };
        private static byte[] GetMimeContent(MimeMessage mime)
        {
            using var ms = new MemoryStream();
            mime.WriteTo(ms);
            return ms.ToArray();
        }
        private static byte[] GetBinaryContent(MimePart part)
        {
            using var ms = new MemoryStream();
            part.Content?.DecodeTo(ms);
            return ms.ToArray();
        }
        // Mirrors GraphMailService.GetBridgeMsgIds exactly -- In-Reply-To first, then References read
        // root-first -- against MimeKit's raw header values instead of Graph's InternetMessageHeaders.
        private static List<string> GetBridgeMsgIds(MimeMessage mime)
        {
            var candidates = new List<string>();

            var inReplyTo = mime.Headers[HeaderId.InReplyTo];
            if (inReplyTo is not null)
                candidates.Add(inReplyTo);

            var references = mime.Headers[HeaderId.References];
            if (references is not null)
            {
                var ids = references.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (var i = ids.Length - 1; i >= 0; i--)
                    if (!candidates.Contains(ids[i]))
                        candidates.Add(ids[i]);
            }

            return candidates;
        }
        // Raw header values (with angle brackets) are used throughout -- deliberately, to match the
        // bracketed format Graph's InternetMessageId already carries, since these values are compared
        // verbatim against msg.TblMessages.FwdMsgId/SentMsgId regardless of which provider wrote them.
    }
}
