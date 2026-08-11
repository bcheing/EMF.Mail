using System;

namespace EMF.Mail.Models
{
    public class MailAccount
    {
        public int AcctId { get; set; }
        public int AppId { get; set; }
        public string AcctTpCode { get; set; } = string.Empty;
        public int OwnerUId { get; set; }
        public string AcctName { get; set; } = string.Empty;
        public string AdmAcctName { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string SecretName { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public bool IsActive { get; set; }
    }

    public class AppUser
    {
        public int UId { get; set; }
        public int? EntId { get; set; }
        public bool IsAdmin { get; set; }
    }

    public class MailMessage
    {
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

    public class MessageResult { public int MsgNo { get; set; } public int SenderId { get; set; } }

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
}
