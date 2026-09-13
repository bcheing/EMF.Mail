using Cheing;
using Cheing.Net.Ai;
using EMF.DMS.Client;
using EMF.FilerSvc;
using EMF.Mail.Models;
using EMF.Mail.Services;
using Microsoft.Extensions.Configuration;
using System;
using System.Reflection;
using System.Threading.Tasks;
using DbBinder = Cheing.Binder;
using FilerDataService = EMF.FilerSvc.Services.DataService;
using MailDataService = EMF.Mail.Services.DataService;
using NetBinder = Cheing.Net.Binder;

namespace EMF.Mail;

public static class Program
{

    public static async Task Main()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json")
            .AddUserSecrets(Assembly.GetExecutingAssembly())
            .AddEnvironmentVariables()
            .Build();

        var dbConfig = config.GetSection("DbConfig").Get<DbConfig>() ?? throw new InvalidOperationException("DbConfig section not found in appsettings.json.");

        var db = new DbBinder(dbConfig);
        var user = await db.GetObjAsync<AppUser>("/ou/user");
        db.UId = user.UId;

        var net = new NetBinder(dbConfig);
        var claude = new ClaudeService(net);
        var classifier = new ClaudeClassifier(claude, db);
        var cmdSvc = new CommandService(claude);
        var filerDataSvc = new FilerDataService(db);
        var dms = new PkgService(db);
        var filer = new Filer(filerDataSvc, classifier, dms);
        var mailDataSvc = new MailDataService(db);
        var conv = new ConversationService(db);

        // One processor for every account, regardless of AppId -- see MessageProcessor for how it reads
        // sender-approval gating and doc-vs-fields package creation from account/data config instead.
        var processor = new MessageProcessor(mailDataSvc, filerDataSvc, cmdSvc, classifier, filer, conv);

        while (true)
        {
            var accounts = await mailDataSvc.GetMailAccountsAsync();
            Tracker.Track($"Loaded {accounts.Count} mail account(s).");

            foreach (var account in accounts)
            {
                IMailService mail = account.ProvCode switch
                {
                    "GRAPH" or "" => new GraphMailService(new GraphAccount(account.AcctName, account.TenantId, account.ClientId), config[$"MailSecrets:{account.SecretName}"] ?? throw new InvalidOperationException($"Secret '{account.SecretName}' not found in configuration.")),
                    "IMAP" => new ImapMailService(new ImapAccount(account.AcctName, account.ImapHost!, account.ImapPort!.Value, account.SmtpHost!, account.SmtpPort!.Value), config[$"MailSecrets:{account.SecretName}"] ?? throw new InvalidOperationException($"Secret '{account.SecretName}' not found in configuration.")),
                    _ => throw new InvalidOperationException($"Account {account.AcctName}: unsupported ProvCode '{account.ProvCode}'.")
                };

                await processor.ProcessAccountAsync(account, mail);
            }

            await Task.Delay(TimeSpan.FromMinutes(1));
        }
    }
}
