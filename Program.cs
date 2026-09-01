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

    public static async Task Main(string[] args) // args reserved for future CLI arguments (e.g. --account, --since).
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json")
            .AddUserSecrets(Assembly.GetExecutingAssembly())
            .Build();

        var dbConfig = config.GetSection("DbConfig").Get<DbConfig>() ?? throw new InvalidOperationException("DbConfig section not found in appsettings.json.");

        var db = new DbBinder(dbConfig);
        var user = await db.GetObjAsync<AppUser>("/ou/user");
        db.UId = user.UId;

        var net = new NetBinder(dbConfig);
        var claude = new ClaudeService(net);
        var classifier = new ClaudeClassifier(claude, db);
        var triageSvc = new TriageService(claude, db, classifier);
        var cmdSvc = new CommandService(claude);
        var filerDataSvc = new FilerDataService(db);
        var dms = new PkgService(db);
        var filer = new Filer(filerDataSvc, classifier, dms);
        var mailDataSvc = new MailDataService(db);
        var conv = new ConversationService(db);

        var processor = new MessageProcessor(mailDataSvc, filerDataSvc, triageSvc, cmdSvc, classifier, filer, conv);

        while (true)
        {
            var accounts = await mailDataSvc.GetMailAccountsAsync();
            Tracker.Track($"Loaded {accounts.Count} mail account(s).");

            foreach (var account in accounts)
            {
                // ProvCode-keyed construction -- GraphMailService is the only implementation today, so
                // every account resolves here. A future provider adds a branch, not a change anywhere else.
                IMailService mail = account.ProvCode switch
                {
                    "GRAPH" or "" => new GraphMailService(account, config[$"MailSecrets:{account.SecretName}"] ?? throw new InvalidOperationException($"Secret '{account.SecretName}' not found in configuration.")),
                    _ => throw new InvalidOperationException($"Account {account.AcctName}: unsupported ProvCode '{account.ProvCode}'.")
                };

                await processor.ProcessAccountAsync(account, mail);
            }

            await Task.Delay(TimeSpan.FromMinutes(1));
        }
    }
}
