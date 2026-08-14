using Cheing;
using Cheing.Net.Ai;
using EMF.Mail.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace EMF.Mail.Services
{
    public class CommandService(ClaudeService claude)
    {
        // Scoped to APPROVE/REJECT only for this pass -- ATTACH/CANCEL/STATUS/CREATE/IGNORE/ROUTE deferred.
        public async Task<Result<CmdResult>> InterpretApprovalReplyAsync(string subject, string body)
        {
            var content = new List<ClaudeContentPart>
            {
                new("text/plain", Text: $"Subject: {subject}\n\n{body}")
            };

            var prompt = """
                An administrator is replying to a request to approve or reject a new email sender for an invoice-processing mailbox.

                The text given is only the administrator's own new reply -- any quoted prior thread has already been stripped out.

                Decide whether this reply approves or rejects the sender.
                If the reply doesn't clearly do either, leave CmdCode null -- do not guess.
                Reference is not used for this decision; leave it null.

                Respond with ONLY a JSON object (no other text) in this exact shape:
                {"CmdCode": "APPROVE" | "REJECT" | null, "Reference": null}
                """;

            return await claude.AskClaude<CmdResult>(content, prompt, []);
        }
    }
}
