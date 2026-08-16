using System;
using System.Collections.Generic;

namespace EMF.Mail.Models
{
    public class MailAccount
    {
        public int AcctId { get; set; }
        public int AppId { get; set; }
        public int OwnerUId { get; set; }
        public string AcctName { get; set; } = string.Empty;
        public string AdmAcctEMail { get; set; } = string.Empty;
        public string GetSenderHistHndName { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string SecretName { get; set; } = string.Empty;
        public DateTime LastPollDT { get; set; }
    }
    public class AppUser
    {
        public int UId { get; set; }
        public int? EntId { get; set; }
        public bool IsAdmin { get; set; }
    }

    public class MailMessage
    {
        public int AcctId { get; set; }
        public string MsgId { get; set; } = string.Empty;
        public string FromAddr { get; set; } = string.Empty;
        public string FromName { get; set; } = string.Empty;
        public DateTime RcptDate { get; set; }
        public string Subject { get; set; } = string.Empty;
        public string? OrigMsgId { get; set; }
        public TriageResult? MsgContext { get; set; }
    }
    public class MessageItem
    {
        public int MsgNo { get; set; }
        public int PkgNo { get; set; }
        public int DocNo { get; set; }
    }
    public class MessageResult { public int MsgNo { get; set; } public int SenderId { get; set; } }

    // Result of /msg/mail/held -- resolves an admin reply back to the message it was forwarded from,
    // via References[0]/In-Reply-To matched against the stored FwdMsgId. CandVendId/CandVendName are
    // the candidate Claude proposed at hold time (from MsgContext) -- reused on a bare confirmation
    // reply instead of re-resolving the vendor name from scratch. MsgContext is the raw persisted json
    // (populated only by /msg/mail/heldbysender) -- deserializes to TriageResult to reuse a message's
    // original classification on approval, without a second Claude call.
    public class HeldMessage
    {
        public int MsgNo { get; set; }
        public int SenderId { get; set; }
        public string MsgId { get; set; } = string.Empty;
        public int? CandVendId { get; set; }
        public string? CandVendName { get; set; }
        public string? MsgContext { get; set; }
    }
    public class ReqStatus
    {
        public int ReqNo { get; set; }
        public int VendId { get; set; }
        public string VendName { get; set; } = string.Empty;
        public string InvcNbr { get; set; } = string.Empty;
        public string QName { get; set; } = string.Empty;
        public string QStatus { get; set; } = string.Empty;
    }

    // One row per (vendor, doc type) this sender has previously sent for -- a sender linked to more than
    // one vendor has multiple rows with different VendId. Distinct by VendId/VendName for the linked-vendor set.
    public class SenderHistory
    {
        public int VendId { get; set; }
        public string VendName { get; set; } = string.Empty;
        public int? DocTypeId { get; set; }
        public int NumDocs { get; set; }
    }
    public class VendorMatch
    {
        public int VendId { get; set; }
        public string VendName { get; set; } = string.Empty;
    }
    public class CmdResult
    {
        public string? CmdCode { get; set; }
        public string? VendorName { get; set; }
    }

    // Full classification output -- also the persisted shape of msg.TblMessages.MsgContext (opaque json,
    // msg/ai schemas never interpret it) so a held message's original classification survives to approval
    // without being reclassified.
    public class TriageResult
    {
        public string? MsgTpCode { get; set; }
        public string? InvcNbr { get; set; }
        public int? VendId { get; set; }
        public string? VendName { get; set; }
        public List<AttachmentLabel> Attachments { get; set; } = [];
    }
    // DocTpId/ExtractedFields are only ever populated for a "Processing" attachment on a known sender's
    // Submission -- that's the one case triage already has the document image open (see TriageService),
    // so classification and extraction happen there instead of Filer reading the same file again later.
    public class AttachmentLabel
    {
        public string FileName { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string GroupId { get; set; } = string.Empty;
        public int? DocTpId { get; set; }
        public Dictionary<string, object>? ExtractedFields { get; set; }
    }

    // Attachment content actually loaded for a Claude call -- replaces the old filenames-only, content-blind
    // approach for classification now that vendor identification may need to read the invoice image itself.
    public record AttachmentContent(string FileName, byte[] Bytes, string MediaType);

    public class EmailReply { public string Body { get; set; } = string.Empty; }
}
