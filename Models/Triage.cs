using System.Collections.Generic;

namespace EMF.Mail.Models
{
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
