using System;

namespace EMF.Mail.Models
{
    public class MailAccount
    {
        public int AcctId { get; set; }
        public int AppId { get; set; }
        public string AcctName { get; set; } = string.Empty;
        public string TennantId { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string SecretName { get; set; } = string.Empty;
    }

    public class User
    {
        public int UId { get; set; }
        public int? EntId { get; set; }
        public bool IsAdmin { get; set; }
    }

    public class Message
    {
        public string MsgId { get; set; } = string.Empty;
        public string FromAddr { get; set; } = string.Empty;
        public string FromName { get; set; } = string.Empty;
        public DateTime RcptDate { get; set; }
        public string Subject { get; set; } = string.Empty;
    }

    public class MessageItem
    {
        public int MsgNo { get; set; }
        public int PkgNo { get; set; }
    }

    public class MessageResult { public int MsgNo { get; set; } }

}
