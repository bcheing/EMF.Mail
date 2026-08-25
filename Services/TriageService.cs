using Cheing;
using Cheing.Net.Ai;
using EMF.FilerSvc.Models;
using EMF.Mail.Models;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbBinder = Cheing.Binder;

namespace EMF.Mail.Services
{
    public class TriageService(ClaudeService claude, DbBinder db, ClaudeClassifier classifier)
    {
        // Vendor identification runs on every message, regardless of how many vendors the sender is
        // already linked to -- skipping it for a one-vendor sender would recreate the same blind-trust
        // flaw this whole design replaces, just narrowed to one-vendor senders instead of all senders.
        // It's a single call: the prompt itself instructs Claude to check text/signature/domain first and
        // fall back to the attachment image only if inconclusive -- not a two-phase C#-side escalation.
        //
        // The attachment image itself is only included in the request at all when the sender has prior
        // history (isKnownSender) -- a first-contact sender hasn't been approved for anything yet, so
        // nothing gets opened on their behalf until an admin confirms the sender. For a known sender,
        // today's escalation order (linked vendors -> text/signature -> domain -> image) still applies.
        //
        // When the image IS included and there's a Processing attachment, this same call also classifies
        // its document type and extracts its fields (via classifier.GetFieldInstructionsAsync, the same
        // formatting ClaudeClassifier.ExtractAsync itself uses) -- the document is already open for vendor
        // purposes, so doing the real extraction work here too means Filer never has to read it again.
        public async Task<Result<TriageResult>> ClassifyAsync(string subject, string body, string fromAddr, List<AttachmentContent> attachments, List<DocType> docTypes, List<SenderHistory> history, string processDesc, Dictionary<int, List<ClaudeFieldSpec>> fieldsByDocType)
        {
            var vendors = history.Select(h => (h.VendId, h.VendName)).Distinct().ToList();
            var isKnownSender = vendors.Count > 0;
            var vendorText = isKnownSender
                ? string.Join("; ", vendors.Select(v => $"VendId {v.VendId} ({v.VendName})"))
                : "This sender is not linked to any vendor yet.";

            var content = new List<ClaudeContentPart>
            {
                new("text/plain", Text: $"From: {fromAddr}\nSubject: {subject}\n\n{body}"),
                new("text/plain", Text: $"Attachments: {(attachments.Count > 0 ? string.Join(", ", attachments.Select(a => a.FileName)) : "none")}"),
                new("text/plain", Text: $"Vendors linked to this sender: {vendorText}")
            };

            if (isKnownSender)
                content.AddRange(attachments.Select(a => new ClaudeContentPart(a.MediaType, Data: a.Bytes)));

            var docTypeList = string.Join(", ", docTypes.Select(t => $"{t.DocTpId} = {t.DocTpName}"));
            var tools = new List<ClaudeTool> { ClaudeClassifier.GetLookupTool(db) };

            var vendorInstructions = isKnownSender
                ? """
                Identify the vendor this email is from. This must be done every time, even if the sender is already linked to exactly one
                vendor below -- do not assume the linked vendor is correct without checking the message itself.
                Check, in this order, stopping as soon as you are confident: the vendor(s) already linked to this sender (below), the email
                text and signature, the sender's domain, and only if still unclear, the attached invoice image itself (attached below).
                """
                : """
                This sender has no prior history, so no attachment image is included in this request -- do not assume one is available.
                Identify the vendor using only the email text, signature, and sender's domain. If that isn't conclusive, leave VendId null
                and give your best-guess VendName if you have one from the text alone -- an administrator needs to confirm this sender
                before any attachment is opened.
                """;

            // Only built when the image is actually in this request (isKnownSender) and there's something to
            // extract from -- otherwise the document types' field lists would just be wasted prompt text on
            // every Inquiry or attachment-less message.
            var extractionInstructions = "";
            if (isKnownSender && attachments.Count > 0 && fieldsByDocType.Count > 0)
            {
                var sections = new List<string>();
                foreach (var docType in docTypes)
                {
                    if (!fieldsByDocType.TryGetValue(docType.DocTpId, out var specs) || specs.Count == 0)
                        continue;

                    var (instructions, _) = await classifier.GetFieldInstructionsAsync(specs);
                    sections.Add($"DocTpId {docType.DocTpId} ({docType.DocTpName}):\n{instructions}");
                }

                if (sections.Count > 0)
                {
                    extractionInstructions = $$"""

                        {{processDesc}}

                        The document image above is already open, so also do this now for every attachment (Processing and
                        Supporting alike): determine which of the following document types it is, and set that attachment's
                        DocTpId to the matched type's id. For "Processing" attachments only, also extract its fields into
                        ExtractedFields, exactly as you would for that type -- "Supporting" attachments are typed but never
                        extracted, so leave their ExtractedFields null. If a "Supporting" attachment doesn't match any known
                        document type, leave its DocTpId null rather than guessing.
                        If this turns out to be an Inquiry, or there are no attachments after grouping, leave every attachment's
                        DocTpId and ExtractedFields null -- there is nothing to type or extract in that case.

                        {{string.Join("\n\n", sections)}}
                        """;
                }
            }

            var prompt = $$"""
                You are triaging an inbound email for an invoice-processing mailbox.

                The email body may include the full prior thread (quoted replies below the newest message), not just the newest reply.
                If relevant information is split across the thread -- for example an invoice number mentioned earlier and an attachment sent later -- look across the whole thread to gather it, rather than only reading the top of the message.

                Decide whether this email is a new Submission ("SUB") or a Status Inquiry ("INQ").
                If attachments are present, it is most likely a Submission. Only classify it as an Inquiry if the message text itself is
                actually asking about the status of a previous submission or invoice -- the absence of an attachment alone is not evidence
                of an inquiry.
                If neither fits confidently, leave MsgTpCode null -- do not guess.

                {{vendorInstructions}}
                Once you have a candidate name, use the get tool with HndName "/ap/lookups/vendid" and the name as the search parameter to
                resolve it to a VendId. If it matches, set VendId and VendName from that result.
                If no confident match is found, leave VendId null but still set VendName to your best-guess vendor name, if you have one --
                if you cannot even guess a name, leave both null.

                If SUB: group the attachments. Each invoice attachment starts its own group, labeled "Processing".
                Any attachment that only supports an invoice already in this email (e.g. a purchase order backing up one of the invoices)
                joins that invoice's group with the same GroupId, labeled "Supporting". Known document types: {{docTypeList}}.

                If INQ: extract the invoice number the sender is asking about, if one is stated. Leave it null if none is given.
                {{extractionInstructions}}

                Respond with ONLY a JSON object (no other text) in this exact shape:
                {"MsgTpCode": "SUB" | "INQ" | null, "InvcNbr": string | null, "VendId": int | null, "VendName": string | null, "Attachments": [{"FileName": string, "Label": "Processing" | "Supporting", "GroupId": string, "DocTpId": int | null, "ExtractedFields": object | null}]}
                """;

            return await claude.AskClaude<TriageResult>(content, prompt, tools);
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
