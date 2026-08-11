using Azure.Identity;
using EMF.Mail.Models;
using Microsoft.Graph;
using Microsoft.Graph.Models;
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

        public async Task<List<GraphMessage>> GetRecentMessagesAsync(int count = 25)
        {
            var result = await _graph.Users[_mailboxUpn].MailFolders["Inbox"].Messages.GetAsync(cfg =>
            {
                cfg.QueryParameters.Top = count;
                cfg.QueryParameters.Orderby = ["receivedDateTime desc"];
                cfg.QueryParameters.Expand = ["attachments"];
                cfg.QueryParameters.Select = ["id", "internetMessageId", "from", "receivedDateTime", "subject", "body", "attachments", "internetMessageHeaders"];
            });

            return result?.Value ?? [];
        }

        // In-Reply-To is a standard RFC 5322 header — provider-agnostic, unlike Graph's own conversationId.
        public static string? GetOrigMsgId(GraphMessage message) =>
            message.InternetMessageHeaders?.FirstOrDefault(h => h.Name?.Equals("In-Reply-To", StringComparison.OrdinalIgnoreCase) == true)?.Value;

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
    }
}
