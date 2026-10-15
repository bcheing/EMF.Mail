using System;
using System.Collections.Generic;

// Header Section ==================================================================================
// Author: Bernie Cheing
//
// EMF.Mail models, grouped in the order a message moves through them.
//
// MAILBOX (provider side: GraphMailService, ImapMailService; SMTP settings ride on ImapAccount)
// Account                    msg.LstAccounts row (/msg/mail/accounts). Bridges both sides: it is a msg row
//                            that holds the provider settings.
// AccountBookmark            LastMsgLink, where the next poll starts (/filer/msg/lastpoll write,
//                            /msg/mail/bookmark read).
// GraphAccount, ImapAccount  connection details built from Account by MailServiceFactory.
// InternetMessageHeader      list-level summary of a mailbox message (DMS Mail page: messages past the bookmark).
// InternetMessage            full inbound message (body, attachments, threading ids); what MessageProcessor works on.
// InternetMessageAttachment  one attachment as the provider returns it.
//
// LOG (msg.TblMessages, msg.TblMsgItems)
// 1. Message                 logs an InternetMessage (/filer/msg/message) and returns a MessageResult (MsgNo,
//                            SenderId). A failed log stops the account run: nothing is processed without a MsgNo.
// 2. MessageItem             links the MsgNo to the packages and documents it created (/filer/msg/msgitems).
// 3. MessageFinalization     terminal state: message type, result type, result text (/filer/msg/finalize).
// 4. MessageEntry            the logged row read back with its result label and severity (/msg/mail/messages,
//                            DMS Mail page).
// MessageBase                the fields Message and MessageEntry share.
//
// APPROVAL (msg.TblApprRequests, msg.TblApprReqItems): message from a sender not yet approved
// ApprRequest, ApprRequestItem  the open hold and the messages held under it.
// CmdResult                  CommandService's reading of the admin's reply (APPROVE/REJECT, party name).
// ApprResolution             the admin's decision, written in one call (/filer/msg/resolvecmd).
// SenderHistory              past parties and document types for a sender (reinforcement hint, not yet wired).
// Party                      the party a sender submits for (type, id, name).
//
// INFO REQUESTS (msg.TblInfoRequests): documents still missing after a submission
// InfoRequest                opens the request (/msg/req/open) and returns an InfoRequestOpenResult.
// InfoRequestBridge          matches the sender's reply back to its open request (/msg/mail/rfibridge).
// ============================================================================================

namespace EMF.Mail.Models
{
    // Classes related to msg.LstAccounts
    public class Account
    {
        public int AcctId { get; set; }
        public int AppId { get; set; }
        public int? EntId { get; set; }
        public int OwnerUId { get; set; }
        public string AcctName { get; set; } = string.Empty;
        public string SecretName { get; set; } = string.Empty;
        public string AdmAcctEMail { get; set; } = string.Empty;
        public string ProvCode { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string? ImapHost { get; set; }
        public int? ImapPort { get; set; }
        public string? SmtpHost { get; set; }
        public int? SmtpPort { get; set; }
        public DateTime LastPollDT { get; set; }
        public string? LastMsgLink { get; set; }
    }
    public class AccountBookmark
    // Payload for /filer/msg/lastpoll and result of /msg/mail/bookmark -- LastPollDT stays as an audit "last run" timestamp; LastMsgLink is
    // the actual delta bookmark now driving what GetChangedMessagesAsync fetches next round.
    {
        public int AcctId { get; set; }
        public DateTime? LastPollDT { get; set; }
        public string? LastMsgLink { get; set; }
    }
    public record GraphAccount(string AcctName, string TenantId, string ClientId);
    public record ImapAccount(string AcctName, string ImapHost, int ImapPort, string SmtpHost, int SmtpPort);

    // Classes related to inbound messages
    public class InternetMessageHeader
    {
        public string ProvMsgId { get; set; } = string.Empty;
        public string? InternetMessageId { get; set; }
        public string FromAddr { get; set; } = string.Empty;
        public string FromName { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public DateTime ReceivedDateTime { get; set; }
        public virtual bool HasAttachments { get; set; }
    }
    public class InternetMessage : InternetMessageHeader
    // Provider-agnostic inbound message, mapped from whatever the underlying mail provider returns
    // (GraphMailService and ImapMailService) so MessageProcessor never touches a
    // provider SDK type directly. ProvMsgId is the provider's own opaque message id (Graph's Id) --
    // used only to make further provider calls (reply/forward/send) against this same
    // message, never persisted. InternetMessageId is the RFC5322 id that IS persisted (msg.TblMessages.MsgId).
    // OrigMsgId/BridgeMsgIds are computed by the provider from its own header mechanics at mapping time,
    // so no raw header list needs to flow through MessageProcessor either.
    {
        public string Body { get; set; } = string.Empty;
        public string UniqueBody { get; set; } = string.Empty;
        public List<InternetMessageAttachment> Attachments { get; set; } = [];
        public override bool HasAttachments => Attachments.Count > 0;
        public string? OrigMsgId { get; set; }
        public List<string> BridgeMsgIds { get; set; } = [];
    }
    public record InternetMessageAttachment(string FileName, byte[] Bytes, string MediaType);

    // Classes related to msg.TblMessages, msg.TblMsgItems
    public class MessageBase
    {
        public string MsgId { get; set; } = string.Empty;
        public string FromAddr { get; set; } = string.Empty;
        public string FromName { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public DateTime RcptDT { get; set; }
        public byte NumAttachments { get; set; }
    }
    public class Message : MessageBase
    {
        public int AcctId { get; set; }
        public string? OrigMsgId { get; set; }
    }
    public class MessageEntry : MessageBase
    {
        public int MsgNo { get; set; }
        public bool IsProcessed { get; set; }
        public bool IsFinal { get; set; }
        public string? MsgTpCode { get; set; }
        public string? ResTpCode { get; set; }
        public string? ResTpLabel { get; set; }
        public string? ResTpSeverity { get; set; }
        public string? MsgResult { get; set; }
    }
    public class MessageItem
    {
        public int MsgNo { get; set; }
        public int PkgNo { get; set; }
        public int? DocNo { get; set; }
    }
    public class MessageResult 
    { 
        public int MsgNo { get; set; } 
        public int SenderId { get; set; } 
    }
    public class MessageFinalization
    // Payload for /filer/msg/finalize -- single write-back point for a message's terminal state. Null
    // fields leave the corresponding column untouched (see the proc). IReqNo absorbs what used to be a
    // separate /filer/msg/linkreply call -- only ever set on the RFI-reply finalize.
    {
        public int MsgNo { get; set; }
        public object? MsgContext { get; set; }
        public string? MsgTpCode { get; set; }
        public int? IReqNo { get; set; }
        public string ResTpCode { get; set; } = string.Empty;
        public string? MsgResult { get; set; }
    }

    // Classes related to msg.TblApprRequests, msg.TblApprReqItems
    public class ApprRequest
    {
        public int ReqNo { get; set; }
        public DateTime ReqDT { get; set; }
        public int SenderId { get; set; }
        public int PartyTpId { get; set; }
        public int? PartyId { get; set; }
        public string? PartyName { get; set; }
        public string MsgId { get; set; } = string.Empty;
        public bool? IsApproved { get; set; }
        public string FromAddr { get; set; } = string.Empty;
        public int NumItems { get; set; }
    }
    public class ApprRequestItem
    {
        public int MsgNo { get; set; }
        public string MsgId { get; set; } = string.Empty;
        public string? MsgContext { get; set; }
    }
    public class ApprResolution
    {
        public int ReqNo { get; set; }
        public int? PartyId { get; set; }
        public bool IsApproved { get; set; }
        public string CmdCode { get; set; } = string.Empty;
        public int AdminMsgNo { get; set; }
        public string? Reference { get; set; }
        public int ResultCode { get; set; }
        public string? ResultMsg { get; set; }
    }


    // Classes related to msg.TblInfoRequests, msg.TblInfoRequestItems
    public class InfoRequest
    // Payload for /msg/req/open -- ReqUId null means system-generated (the only origin EMF.Mail creates
    // today; NotNo/user-initiated-via-PkgNotify origin is deferred, see ProjectContext).
    {
        public int PkgNo { get; set; }
        public int? MsgNo { get; set; }
        public int? NotNo { get; set; }
        public string SentMsgId { get; set; } = string.Empty;
        public string SentTo { get; set; } = string.Empty;
        public int? ReqUId { get; set; }
        public int ConvNo { get; set; }
    }
    public class InfoRequestOpenResult
    {
        public int IReqNo { get; set; }
    }
    public class InfoRequestBridge
    // Result of /msg/mail/rfibridge -- resolves a vendor's reply back to the open msg.TblInfoRequests row
    // it's answering, via In-Reply-To/References matched against SentMsgId. SentTo is returned so the
    // caller can confirm the reply's sender matches who the RFI was actually sent to -- sufficient
    // authorization for this one thread even for a sender with no other approval on file (e.g. a
    // freight forwarder), without needing a static whitelist for every ad hoc external party.
    {
        public int IReqNo { get; set; }
        public int PkgNo { get; set; }
        public int? MsgNo { get; set; }
        public int ConvNo { get; set; }
        public string SentTo { get; set; } = string.Empty;
    }

    // Misc
    public class SenderHistory
    {
        public int PartyId { get; set; }
        public string PartyName { get; set; } = string.Empty;
        public int? DocTypeId { get; set; }
        public int NumDocs { get; set; }
    }
    public class CmdResult
    {
        public string? CmdCode { get; set; }
        public string? PartyName { get; set; }
    }
    public record Party(EMF.DMS.Client.PartyType Type, int Id, string? Name);
}
