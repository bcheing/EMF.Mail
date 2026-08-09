using Azure.Identity;
using EMF.Mail.Models;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using System.Collections.Generic;
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
            var credential = new ClientSecretCredential(account.TennantId, account.ClientId, clientSecret);
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
            });

            return result?.Value ?? [];
        }

        public Task FlagProcessedAsync(string messageId) => _graph.Users[_mailboxUpn].Messages[messageId].PatchAsync(new GraphMessage
        {
            Flag = new FollowupFlag { FlagStatus = FollowupFlagStatus.Flagged }
        });

        public Task ReplyAsync(string messageId, string comment) => _graph.Users[_mailboxUpn].Messages[messageId].Reply.PostAsync(new() { Comment = comment });
    }
}
