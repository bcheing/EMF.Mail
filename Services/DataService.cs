using Cheing;
using EMF.Mail.Models;
using System.Threading.Tasks;
using DbBinder = Cheing.Binder;

namespace EMF.MailSvc.Services
{
    public class DataService(DbBinder db)
    {
        public Task<Result<MessageResult>> SaveMessageAsync(Message msg) => db.PutObjAsync<MessageResult>(new { HndName = "/filer/msg/message", msg });
        public Task<Result> SaveMessageItemAsync(MessageItem item) => db.PutAsync(new { HndName = "/filer/msg/items", item });

    }
}
