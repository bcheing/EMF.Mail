using EMF.Mail.Models;
using Microsoft.Extensions.Configuration;
using System;

namespace EMF.Mail.Services
{
    public static class MailServiceFactory
    {
        public static IMailService GetMailService(Account account, IConfiguration config) => account.ProvCode switch
        {
            "GRAPH" or "" => new GraphMailService(new GraphAccount(account.AcctName, account.TenantId, account.ClientId), config[$"MailSecrets:{account.SecretName}"] ?? throw new InvalidOperationException($"Secret '{account.SecretName}' not found in configuration.")),
            "IMAP" => new ImapMailService(new ImapAccount(account.AcctName, account.ImapHost!, account.ImapPort!.Value, account.SmtpHost!, account.SmtpPort!.Value), config[$"MailSecrets:{account.SecretName}"] ?? throw new InvalidOperationException($"Secret '{account.SecretName}' not found in configuration.")),
            _ => throw new InvalidOperationException($"Account {account.AcctName}: unsupported ProvCode '{account.ProvCode}'.")
        };
    }
}
