using Azure.Identity;
using EMF.Mail.Models;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.MailFolders.Item.Messages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GraphMessage = Microsoft.Graph.Models.Message;

namespace EMF.Mail.Services
{
    public class GraphMailService
    {
        private readonly GraphServiceClient _graph;
        private readonly string _mailboxUpn;

        public GraphMailService(MailAccount account, string clientSecret)
        {
            var credential = new ClientSecretCredential(account.TenantId, account.ClientId, clientSecret);
            _graph = new GraphServiceClient(credential);
            _mailboxUpn = account.AcctName;
        }

        public async Task<List<GraphMessage>> GetRecentMessagesAsync(DateTime since)
        {
            var messages = new List<GraphMessage>();

            var result = await _graph.Users[_mailboxUpn].MailFolders["Inbox"].Messages.GetAsync(cfg =>
            {
                cfg.QueryParameters.Filter = $"receivedDateTime gt {since:yyyy-MM-ddTHH:mm:ssZ}";
                cfg.QueryParameters.Orderby = ["receivedDateTime asc"];
                cfg.QueryParameters.Expand = ["attachments"];
                cfg.QueryParameters.Select = ["id", "internetMessageId", "from", "receivedDateTime", "subject", "body", "uniqueBody", "attachments", "internetMessageHeaders"];
            });

            while (result?.Value is { Count: > 0 })
            {
                messages.AddRange(result.Value);

                if (string.IsNullOrEmpty(result.OdataNextLink))
                    break;

                result = await new MessagesRequestBuilder(result.OdataNextLink, _graph.RequestAdapter).GetAsync();
            }

            return messages;
        }

        // In-Reply-To is a standard RFC 5322 header -- provider-agnostic, unlike Graph's own conversationId.
        public static string? GetOrigMsgId(GraphMessage message) =>
            message.InternetMessageHeaders?.FirstOrDefault(h => h.Name?.Equals("In-Reply-To", StringComparison.OrdinalIgnoreCase) == true)?.Value;

        // References accumulates every ancestor's Message-ID as a thread grows (parent's References + parent's
        // Message-ID, per RFC 5322 3.6.4), so the first entry is always the root message no matter how many
        // replies deep -- this is what lets an admin's Nth reply still resolve back to the original hold
        // without walking the chain ourselves. Falls back to In-Reply-To for a first-hop reply (same value).
        public static List<string> GetBridgeMsgIds(GraphMessage message)
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
        public async Task<GraphMessage?> GetMessageByIdAsync(string internetMessageId)
        {
            var result = await _graph.Users[_mailboxUpn].Messages.GetAsync(cfg =>
            {
                cfg.QueryParameters.Filter = $"internetMessageId eq '{internetMessageId}'";
                cfg.QueryParameters.Expand = ["attachments"];
                cfg.QueryParameters.Select = ["id", "internetMessageId", "from", "receivedDateTime", "subject", "body", "uniqueBody", "attachments", "internetMessageHeaders"];
            });

            return result?.Value?.FirstOrDefault();
        }

        public Task FlagProcessedAsync(string messageId) => _graph.Users[_mailboxUpn].Messages[messageId].PatchAsync(new GraphMessage
        {
            Flag = new FollowupFlag { FlagStatus = FollowupFlagStatus.Flagged }
        });

        public Task MarkNeedsReviewAsync(string messageId) => _graph.Users[_mailboxUpn].Messages[messageId].PatchAsync(new GraphMessage
        {
            Categories = ["NeedsReview"]
        });

        public Task ReplyAsync(string messageId, string comment) => _graph.Users[_mailboxUpn].Messages[messageId].Reply.PostAsync(new() { Comment = comment });

        public Task ForwardAsync(string messageId, string toAddr, string comment) => _graph.Users[_mailboxUpn].Messages[messageId].Forward.PostAsync(new()
        {
            Comment = comment,
            ToRecipients = [new Recipient { EmailAddress = new EmailAddress { Address = toAddr } }]
        });

        // /forward is fire-and-forget (202, no body) so it can't hand back the sent message's id. createForward
        // returns the draft as a full message object (id assigned at creation), which we then send explicitly --
        // that id becomes FwdMsgId, the anchor the admin's reply chain gets matched back against.
        // comment now varies by call site -- whether Claude proposed a candidate vendor or not -- instead of
        // being a single hardcoded sentence.
        public async Task<string?> SendApprovalRequestAsync(string messageId, string toAddr, string comment)
        {
            var draft = await _graph.Users[_mailboxUpn].Messages[messageId].CreateForward.PostAsync(new()
            {
                Comment = comment,
                ToRecipients = [new Recipient { EmailAddress = new EmailAddress { Address = toAddr } }]
            });

            if (draft?.Id is null) return null;

            await _graph.Users[_mailboxUpn].Messages[draft.Id].Send.PostAsync();

            return draft.InternetMessageId;
        }
    }
}
