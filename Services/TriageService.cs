using Cheing;
using Cheing.Net.Ai;
using EMF.FilerSvc.Models;
using EMF.Mail.Models;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EMF.Mail.Services
{
    public class TriageService(ClaudeService claude)
    {
        // Content-blind by design -- filenames and text only, never attachment bytes. Bytes only travel to Claude
        // later, once per doc actually needing extraction (see Filer.ProcessDocumentAsync).
        public async Task<Result<TriageResult>> ClassifyAsync(string subject, string body, string fromAddr, List<string> fileNames, List<DocType> docTypes, List<SenderHistory> history)
        {
            var content = new List<ClaudeContentPart>
            {
                new("text/plain", Text: $"From: {fromAddr}\nSubject: {subject}\n\n{body}"),
                new("text/plain", Text: $"Attachments: {(fileNames.Count > 0 ? string.Join(", ", fileNames) : "none")}"),
                new("text/plain", Text: $"Sender history: {(history.Count > 0 ? string.Join("; ", history.Select(h => $"AppId {h.AppId}, DocTypeId {h.DocTypeId}, {h.NumDocs} prior doc(s)")) : "none on file")}")
            };

            var docTypeList = string.Join(", ", docTypes.Select(t => $"{t.DocTpId} = {t.DocTpName}"));

            var prompt = $$"""
                You are triaging an inbound email for an invoice-processing mailbox.

                The email body may include the full prior thread (quoted replies below the newest message), not just the newest reply.
                If relevant information is split across the thread -- for example an invoice number mentioned earlier and an attachment sent later -- look across the whole thread to gather it, rather than only reading the top of the message.

                Decide whether this email is a new Submission ("SUB") or a Status Inquiry ("INQ").
                If no attachments are present, it is most likely an Inquiry. If attachments are present, it is most likely a Submission.
                If neither fits confidently, leave MsgTpCode null -- do not guess.

                If SUB: group the attachments. Each invoice attachment starts its own group, labeled "Processing".
                Any attachment that only supports an invoice already in this email (e.g. a purchase order backing up one of the invoices)
                joins that invoice's group with the same GroupId, labeled "Supporting". Known document types: {docTypeList}.
                Use the sender history above as a hint for what this sender typically sends, not a rule.

                If INQ: extract the invoice number the sender is asking about, if one is stated. Leave it null if none is given.

                Respond with ONLY a JSON object (no other text) in this exact shape:
                {"MsgTpCode": "SUB" | "INQ" | null, "InvcNbr": string | null, "Attachments": [{"FileName": string, "Label": "Processing" | "Supporting", "GroupId": string}]}
                """;

            return await claude.AskClaude<TriageResult>(content, prompt, []);
        }

        public async Task<Result<string>> ComposeInquiryReplyAsync(List<ReqStatus> matches)
        {
            var summary = matches.Count == 0
                ? "No matching request was found for this sender."
                : string.Join("\n", matches.Select(m => $"ReqNo {m.ReqNo}, Vendor {m.VendName}, Invoice {m.InvcNbr}, Status {m.QStatus} ({m.QName})"));

            var content = new List<ClaudeContentPart> { new("text/plain", Text: summary) };
            var prompt = """
                Write a short, polite email reply to a vendor asking about the status of their invoice submission, using only the information given above.

                Respond with ONLY a JSON object (no other text) in this exact shape:
                {"Body": string}
                """;

            var result = await claude.AskClaude<EmailReply>(content, prompt, []);
            if (result.IsFailure) return Result.Fail<string>(result.Message);

            return Result.Ok(result.Value.Body);
        }
    }
}
