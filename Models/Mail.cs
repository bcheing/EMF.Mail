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
    }
    public class MessageItem
    {
        public int MsgNo { get; set; }
        public int PkgNo { get; set; }
        public int DocNo { get; set; }
    }
    public class MessageResult { public int MsgNo { get; set; } public int SenderId { get; set; } public bool IsApproved { get; set; } }

    // Result of /msg/mail/held -- resolves an admin reply back to the message it was forwarded from,
    // via References[0]/In-Reply-To matched against the stored FwdMsgId.

    public class HeldMessage { public int MsgNo { get; set; } public int SenderId { get; set; } public string MsgId { get; set; } = string.Empty; }
    public class ReqStatus
    {
        public int ReqNo { get; set; }
        public int VendId { get; set; }
        public string VendName { get; set; } = string.Empty;
        public string InvcNbr { get; set; } = string.Empty;
        public string QName { get; set; } = string.Empty;
        public string QStatus { get; set; } = string.Empty;
    }
    public class SenderHistory
    {
        public int AppId { get; set; }
        public int? DocTypeId { get; set; }
        public int NumDocs { get; set; }
    }
    public class CmdResult
    {
        public string? CmdCode { get; set; }
        public string? Reference { get; set; }
    }
    public class TriageResult
    {
        public string? MsgTpCode { get; set; }
        public string? InvcNbr { get; set; }
        public List<AttachmentLabel> Attachments { get; set; } = [];
    }
    public class AttachmentLabel
    {
        public string FileName { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string GroupId { get; set; } = string.Empty;
    }
    public class EmailReply { public string Body { get; set; } = string.Empty; }
}
