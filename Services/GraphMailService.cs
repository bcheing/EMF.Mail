using Azure.Identity;
using EMF.FilerSvc;
using EMF.Mail.Models;
using Microsoft.Graph;
using Microsoft.Graph.Users.Item.MailFolders.Item.Messages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EmailAddress = Microsoft.Graph.Models.EmailAddress;
using FileAttachment = Microsoft.Graph.Models.FileAttachment;
using FollowupFlag = Microsoft.Graph.Models.FollowupFlag;
using FollowupFlagStatus = Microsoft.Graph.Models.FollowupFlagStatus;
using GraphMessage = Microsoft.Graph.Models.Message;
using Recipient = Microsoft.Graph.Models.Recipient;

namespace EMF.Mail.Services
{
    public class GraphMailService : IMailService
    {
        private readonly GraphServiceClient _graph;
        private readonly string _mailboxUpn;

        public GraphMailService(MailAccount account, string clientSecret)
        {
            var credential = new ClientSecretCredential(account.TenantId, account.ClientId, clientSecret);
            _graph = new GraphServiceClient(credential);
            _mailboxUpn = account.AcctName;
        }
        private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";

        // Maps a Graph SDK message into the provider-agnostic Message -- OrigMsgId/BridgeMsgIds are
        // resolved here, from Graph's own header mechanics, so MessageProcessor never touches a raw
        // header list. A future provider resolves the same two fields however its own library exposes
        // In-Reply-To/References.
        private static Message MapMessage(GraphMessage message) => new()
        {
            ProvMsgId = message.Id ?? "",
            InternetMessageId = message.InternetMessageId,
            FromAddr = message.From?.EmailAddress?.Address ?? "",
            FromName = message.From?.EmailAddress?.Name ?? "",
            ReceivedDateTime = message.ReceivedDateTime?.DateTime ?? DateTime.UtcNow,
            Subject = message.Subject ?? "",
            Body = message.Body?.Content ?? "",
            UniqueBody = message.UniqueBody?.Content ?? message.Body?.Content ?? "",
            Attachments = (message.Attachments?.OfType<FileAttachment>() ?? [])
                .Where(a => a.ContentBytes is not null)
                .Select(a => new AttachmentContent(a.Name ?? "", a.ContentBytes!, Filer.GetMediaType(a.Name ?? "")))
                .ToList(),
            OrigMsgId = GetOrigMsgId(message),
            BridgeMsgIds = GetBridgeMsgIds(message)
        };

        // Delta query replaces the old timestamp-watermark poll. Bootstrap round (lastMsgLink null) hand-builds
        // the initial request URL rather than using the typed QueryParameters -- the v5 SDK has no typed property
        // for changeType (see msgraph-sdk-dotnet #2195/#1689), so this is the only reliable way to apply it. Scoped
        // to "received from right now on" (the one $filter shape message delta supports) plus changeType=created,
        // so this returns zero messages immediately and an @odata.deltaLink that only ever surfaces new mail from
        // here on -- our own FlagProcessedAsync/MarkNeedsReviewAsync writes, and the sender's own flag/read/move
        // actions, never come back as phantom "changes" to reprocess. changeType can only be set on this first
        // call of a round -- it rides along inside every later @odata.nextLink/@odata.deltaLink automatically.
        public async Task<(List<Message> Messages, string DeltaLink)> GetChangedMessagesAsync(string? lastMsgLink)
        {
            var messages = new List<GraphMessage>();

            var builder = _graph.Users[_mailboxUpn].MailFolders["Inbox"].Messages.Delta;

            string? select = "id,internetMessageId,from,receivedDateTime,subject,body,uniqueBody,attachments,internetMessageHeaders";

            var result = lastMsgLink is null
                ? await builder.WithUrl($"{GraphBaseUrl}/users/{Uri.EscapeDataString(_mailboxUpn)}/mailFolders('Inbox')/messages/delta" +
                      $"?changeType=created&$filter={Uri.EscapeDataString($"receivedDateTime ge {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}")}" +
                      $"&$expand=attachments&$select={select}").GetAsDeltaGetResponseAsync()
                : await builder.WithUrl(lastMsgLink).GetAsDeltaGetResponseAsync();

            while (result?.Value is { Count: > 0 })
            {
                messages.AddRange(result.Value);

                if (string.IsNullOrEmpty(result.OdataNextLink))
                    break;

                result = await builder.WithUrl(result.OdataNextLink).GetAsDeltaGetResponseAsync();
            }

            return (messages.Select(MapMessage).ToList(), result?.OdataDeltaLink ?? lastMsgLink ?? "");
        }

        // In-Reply-To is a standard RFC 5322 header -- provider-agnostic, unlike Graph's own conversationId.
        private static string? GetOrigMsgId(GraphMessage message) =>
            message.InternetMessageHeaders?.FirstOrDefault(h => h.Name?.Equals("In-Reply-To", StringComparison.OrdinalIgnoreCase) == true)?.Value;

        // References accumulates every ancestor's Message-ID as a thread grows (parent's References + parent's
        // Message-ID, per RFC 5322 3.6.4), so the first entry is always the root message no matter how many
        // replies deep -- this is what lets an admin's Nth reply still resolve back to the original hold
        // without walking the chain ourselves. Falls back to In-Reply-To for a first-hop reply (same value).
        // Also reused for RFI bridging -- same header mechanics apply to a vendor's reply chain.
        private static List<string> GetBridgeMsgIds(GraphMessage message)
        {
            var candidates = new List<string>();

            var inReplyTo = GetOrigMsgId(message);
            if (inReplyTo is not null)
                candidates.Add(inReplyTo);

            var references = message.InternetMessageHeaders?.FirstOrDefault(h => h.Name?.Equals("References", StringComparison.OrdinalIgnoreCase) == true)?.Value;
            if (references is not null)
            {
                var ids = references.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (var i = ids.Length - 1; i >= 0; i--)
                    if (!candidates.Contains(ids[i]))
                        candidates.Add(ids[i]);
            }

            return candidates;
        }

        // Used to reprocess an originally-held message after admin approval -- the message itself was
        // never staged, so this is a direct filtered lookup (not a sweep) via the MsgId saved at hold time.
        public async Task<Message?> GetMessageByIdAsync(string internetMessageId)
        {
            var result = await _graph.Users[_mailboxUpn].Messages.GetAsync(cfg =>
            {
                cfg.QueryParameters.Filter = $"internetMessageId eq '{internetMessageId}'";
                cfg.QueryParameters.Expand = ["attachments"];
                cfg.QueryParameters.Select = ["id", "internetMessageId", "from", "receivedDateTime", "subject", "body", "uniqueBody", "attachments", "internetMessageHeaders"];
            });

            var message = result?.Value?.FirstOrDefault();
            return message is null ? null : MapMessage(message);
        }

        public Task FlagProcessedAsync(string provMsgId) => _graph.Users[_mailboxUpn].Messages[provMsgId].PatchAsync(new GraphMessage
        {
            Flag = new FollowupFlag { FlagStatus = FollowupFlagStatus.Flagged }
        });

        public Task MarkNeedsReviewAsync(string provMsgId) => _graph.Users[_mailboxUpn].Messages[provMsgId].PatchAsync(new GraphMessage
        {
            Categories = ["NeedsReview"]
        });

        public Task ReplyAsync(string provMsgId, string comment) => _graph.Users[_mailboxUpn].Messages[provMsgId].Reply.PostAsync(new() { Comment = comment });

        public Task ForwardAsync(string provMsgId, string toAddr, string comment) => _graph.Users[_mailboxUpn].Messages[provMsgId].Forward.PostAsync(new()
        {
            Comment = comment,
            ToRecipients = [new Recipient { EmailAddress = new EmailAddress { Address = toAddr } }]
        });

        // /forward is fire-and-forget (202, no body) so it can't hand back the sent message's id. createForward
        // returns the draft as a full message object (id assigned at creation), which we then send explicitly --
        // that id becomes FwdMsgId, the anchor the admin's reply chain gets matched back against.
        // comment now varies by call site -- whether Claude proposed a candidate vendor or not -- instead of
        // being a single hardcoded sentence.
        public async Task<string?> SendApprovalRequestAsync(string provMsgId, string toAddr, string comment)
        {
            var draft = await _graph.Users[_mailboxUpn].Messages[provMsgId].CreateForward.PostAsync(new()
            {
                Comment = comment,
                ToRecipients = [new Recipient { EmailAddress = new EmailAddress { Address = toAddr } }]
            });

            if (draft?.Id is null) return null;

            await _graph.Users[_mailboxUpn].Messages[draft.Id].Send.PostAsync();

            return draft.InternetMessageId;
        }

        // Same capture problem as SendApprovalRequestAsync, same fix -- /reply is fire-and-forget too, so
        // an RFI (the ask for missing attachments) uses createReply+send instead, to capture the sent
        // message's own InternetMessageId as msg.TblInfoRequests.SentMsgId, the bridge target for whichever
        // reply the vendor eventually sends back.
        public async Task<string?> SendInfoRequestAsync(string provMsgId, string comment)
        {
            var draft = await _graph.Users[_mailboxUpn].Messages[provMsgId].CreateReply.PostAsync(new() { Comment = comment });

            if (draft?.Id is null) return null;

            await _graph.Users[_mailboxUpn].Messages[draft.Id].Send.PostAsync();

            return draft.InternetMessageId;
        }
    }
}
