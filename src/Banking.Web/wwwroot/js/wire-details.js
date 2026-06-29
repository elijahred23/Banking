(() => {
    const region = document.getElementById("wire-live-region");
    if (!region) return;

    const content = document.getElementById("wire-details-content");
    const stateText = document.getElementById("live-state-text");
    const state = region.querySelector(".live-state");
    const wireId = region.dataset.wireId;
    let refreshInProgress = false;
    let refreshQueued = false;

    const fieldGlossary = {
        Fr: ["From", "Financial institution that sent the message."],
        To: ["To", "Financial institution intended to receive the message."],
        MmbId: ["Member ID", "Clearing-system member identifier. For U.S. payment rails this is typically the bank's nine-digit ABA routing number."],
        BICFI: ["BIC", "Business Identifier Code identifying a financial institution on the SWIFT network."],
        BizMsgIdr: ["Business message ID", "Unique identifier assigned to this business message by the sender."],
        MsgDefIdr: ["Message definition", "ISO 20022 message name and version that defines the structure of the document, such as pacs.008.001.08."],
        BizSvc: ["Business service", "Market practice or network rules under which the message is exchanged."],
        CreDt: ["Header created", "Date and time when the Business Application Header was created."],
        MsgId: ["Message ID", "Identifier used to distinguish this message from other messages in the same business process."],
        CreDtTm: ["Created", "Date and time when the business message content was created."],
        NbOfTxs: ["Transaction count", "Number of individual transactions included in this message."],
        SttlmMtd: ["Settlement method", "How settlement occurs. CLRG means through a clearing system; INDA means through the instructed agent."],
        InstrId: ["Instruction ID", "Identifier assigned by the instructing party to this payment instruction."],
        EndToEndId: ["End-to-end ID", "Reference that follows the payment from its originator through to the beneficiary."],
        UETR: ["UETR", "Unique End-to-end Transaction Reference used to identify and track a payment across institutions."],
        IntrBkSttlmAmt: ["Interbank settlement amount", "Amount transferred between the debtor and creditor financial institutions."],
        IntrBkSttlmDt: ["Settlement date", "Date on which funds are due to settle between the financial institutions."],
        ChrgBr: ["Charge bearer", "Specifies who pays transaction charges. SLEV follows service-level rules; SHAR splits charges between the parties."],
        Dbtr: ["Debtor", "Person or organization whose account is debited for the payment."],
        DbtrAcct: ["Debtor account", "Account from which the payment is made."],
        DbtrAgt: ["Debtor agent", "Financial institution servicing the debtor's account."],
        Cdtr: ["Creditor", "Person or organization receiving the payment."],
        CdtrAcct: ["Creditor account", "Account to which the payment is credited."],
        CdtrAgt: ["Creditor agent", "Financial institution servicing the creditor's account."],
        Nm: ["Name", "Name of the person, organization, or financial institution in this context."],
        Id: ["Identifier", "Identifier of the account or party in this context."],
        IBAN: ["IBAN", "International Bank Account Number identifying a specific customer account."],
        TwnNm: ["Town", "Town or city in the party's postal address."],
        Ctry: ["Country", "Two-letter ISO country code in the party's postal address."],
        OrgnlMsgId: ["Original message ID", "Message identifier from the payment message to which this status refers."],
        OrgnlMsgNmId: ["Original message type", "ISO 20022 definition identifier of the original payment message."],
        OrgnlInstrId: ["Original instruction ID", "Instruction identifier from the original payment."],
        OrgnlUETR: ["Original UETR", "Unique End-to-end Transaction Reference from the original payment."],
        TxSts: ["Transaction status", "Current ISO 20022 status of the transaction, such as accepted, pending, or rejected."],
        Prtry: ["Proprietary reason", "Network- or application-specific status reason code or description."],
        AddtlInf: ["Additional information", "Supplementary status or network reference information."]
    };

    const sectionNames = {
        AppHdr: "Business application header",
        GrpHdr: "Group header",
        PmtId: "Payment identification",
        CdtTrfTxInf: "Credit transfer",
        OrgnlGrpInfAndSts: "Original message status",
        TxInfAndSts: "Transaction status",
        StsRsnInf: "Status reason"
    };

    function setConnectionState(text, connected) {
        stateText.textContent = text;
        state.classList.toggle("live-connected", connected);
    }

    function escapeHtml(value) {
        return value.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;");
    }

    function highlightXmlTag(value) {
        const tag = value.match(/^(<\/?)([\w:.-]+)([\s\S]*?)(\/?>)$/);
        if (!tag) return escapeHtml(value);

        let attributes = "";
        let cursor = 0;
        const attributePattern = /([\w:.-]+)(\s*=\s*)("[^"]*"|'[^']*')/g;
        for (const match of tag[3].matchAll(attributePattern)) {
            attributes += escapeHtml(tag[3].slice(cursor, match.index));
            attributes += `<span class="xml-attr">${escapeHtml(match[1])}</span>`;
            attributes += escapeHtml(match[2]);
            attributes += `<span class="xml-value">${escapeHtml(match[3])}</span>`;
            cursor = match.index + match[0].length;
        }
        attributes += escapeHtml(tag[3].slice(cursor));

        return `${escapeHtml(tag[1])}<span class="xml-tag">${escapeHtml(tag[2])}</span>${attributes}${escapeHtml(tag[4])}`;
    }

    function highlightXml(root = content) {
        root.querySelectorAll(".xml-viewer code").forEach(code => {
            const xml = code.dataset.rawXml ?? code.textContent;
            code.dataset.rawXml = xml;
            code.innerHTML = xml
                .split(/(<[^>]+>)/g)
                .map(part => part.startsWith("<") ? highlightXmlTag(part) : escapeHtml(part))
                .join("");
        });
    }

    function localName(node) {
        return node.localName || node.nodeName.split(":").pop();
    }

    function previewSection(element) {
        let current = element.parentElement;
        while (current) {
            const name = localName(current);
            if (sectionNames[name]) return sectionNames[name];
            current = current.parentElement;
        }
        return "Message details";
    }

    function fieldLabel(element) {
        const name = localName(element);
        if (fieldGlossary[name]) return fieldGlossary[name][0];
        return name.replace(/([a-z0-9])([A-Z])/g, "$1 $2");
    }

    function addPreviewField(list, element) {
        const name = localName(element);
        const row = document.createElement("div");
        row.className = "iso-preview-field";

        const term = document.createElement("dt");
        const termText = document.createElement("span");
        termText.textContent = fieldLabel(element);
        term.append(termText);

        if (fieldGlossary[name]) {
            const help = document.createElement("button");
            help.type = "button";
            help.className = "field-help";
            help.textContent = "?";
            help.setAttribute("aria-label", `About ${fieldGlossary[name][0]}`);
            help.setAttribute("data-bs-toggle", "tooltip");
            help.setAttribute("data-bs-placement", "top");
            help.setAttribute("data-bs-title", fieldGlossary[name][1]);
            term.append(help);
        }

        const tag = document.createElement("span");
        tag.className = "field-code";
        tag.textContent = name;
        term.append(tag);

        const definition = document.createElement("dd");
        definition.textContent = element.textContent.trim() || "—";
        if (element.attributes.length) {
            const attributes = document.createElement("small");
            attributes.textContent = Array.from(element.attributes)
                .filter(attribute => !attribute.name.startsWith("xmlns"))
                .map(attribute => `${attribute.name}: ${attribute.value}`)
                .join(" · ");
            if (attributes.textContent) definition.append(attributes);
        }

        row.append(term, definition);
        list.append(row);
    }

    function showPreview(message) {
        const modalElement = document.getElementById("iso-message-preview");
        const body = document.getElementById("iso-message-preview-body");
        const title = document.getElementById("iso-message-preview-title");
        const code = message.querySelector(".xml-viewer code");
        const parsed = new DOMParser().parseFromString(
            code.dataset.rawXml ?? code.textContent,
            "application/xml"
        );
        const parseError = parsed.querySelector("parsererror");

        title.textContent = `${message.dataset.messageType} values`;
        body.replaceChildren();
        if (parseError) {
            const error = document.createElement("p");
            error.className = "iso-preview-error";
            error.textContent = "This XML could not be parsed, so a value preview is unavailable.";
            body.append(error);
        } else {
            const leaves = Array.from(parsed.getElementsByTagName("*"))
                .filter(element => !Array.from(element.children).length && element.textContent.trim());
            const groups = new Map();
            leaves.forEach(element => {
                const section = previewSection(element);
                if (!groups.has(section)) groups.set(section, []);
                groups.get(section).push(element);
            });

            const summary = document.createElement("div");
            summary.className = "iso-preview-summary";
            const type = document.createElement("span");
            type.className = "iso-preview-type";
            type.textContent = message.dataset.messageType;
            const count = document.createElement("span");
            count.textContent = `${leaves.length} populated fields`;
            summary.append(type, count);

            const sections = document.createElement("div");
            sections.className = "iso-preview-sections";
            groups.forEach((elements, name) => {
                const section = document.createElement("section");
                section.className = "iso-preview-section";
                const heading = document.createElement("h3");
                heading.textContent = name;
                const list = document.createElement("dl");
                list.className = "iso-preview-fields";
                elements.forEach(element => addPreviewField(list, element));
                section.append(heading, list);
                sections.append(section);
            });
            body.append(summary, sections);
        }

        modalElement.querySelectorAll('[data-bs-toggle="tooltip"]').forEach(element => {
            bootstrap.Tooltip.getOrCreateInstance(element, { container: modalElement });
        });
        bootstrap.Modal.getOrCreateInstance(modalElement).show();
    }

    async function refresh() {
        if (refreshInProgress) {
            refreshQueued = true;
            return;
        }
        refreshInProgress = true;
        try {
            const response = await fetch(region.dataset.fragmentUrl, {
                headers: { "X-Requested-With": "XMLHttpRequest" },
                cache: "no-store"
            });
            if (!response.ok) throw new Error(`Wire refresh failed (${response.status})`);
            content.innerHTML = await response.text();
            wireMessageActions(content);
            highlightXml(content);
            setConnectionState(`Live · updated ${new Date().toLocaleTimeString()}`, true);
        } catch (error) {
            console.error(error);
            setConnectionState("Live update unavailable · retrying", false);
        } finally {
            refreshInProgress = false;
            if (refreshQueued) {
                refreshQueued = false;
                void refresh();
            }
        }
    }

    function wireMessageActions(root = content) {
        root.querySelectorAll(".iso-message").forEach(message => {
            const xml = () => {
                const code = message.querySelector(".xml-viewer code");
                return code.dataset.rawXml ?? code.textContent;
            };
            message.querySelector(".preview-xml")?.addEventListener("click", () => showPreview(message));
            message.querySelector(".copy-xml")?.addEventListener("click", async event => {
                await navigator.clipboard.writeText(xml());
                const button = event.currentTarget;
                const original = button.textContent;
                button.textContent = "Copied";
                setTimeout(() => button.textContent = original, 1200);
            });
            message.querySelector(".download-xml")?.addEventListener("click", () => {
                const blob = new Blob([xml()], { type: "application/xml;charset=utf-8" });
                const link = document.createElement("a");
                link.href = URL.createObjectURL(blob);
                link.download = `${message.dataset.messageType}-${message.dataset.messageId}.xml`;
                link.click();
                URL.revokeObjectURL(link.href);
            });
        });
    }

    wireMessageActions();
    highlightXml();

    if (!window.signalR) {
        setConnectionState("Live client unavailable · checking periodically", false);
        setInterval(refresh, 15000);
        return;
    }

    const connection = new signalR.HubConnectionBuilder()
        .withUrl(`/hubs/wires?wireId=${encodeURIComponent(wireId)}`)
        .withAutomaticReconnect([0, 1000, 3000, 10000])
        .build();

    connection.on("wireUpdated", refresh);
    connection.onreconnecting(() => setConnectionState("Reconnecting to live updates…", false));
    connection.onreconnected(() => {
        setConnectionState("Live", true);
        void refresh();
    });
    connection.onclose(() => setConnectionState("Live update disconnected", false));

    async function start() {
        try {
            await connection.start();
            setConnectionState("Live", true);
        } catch (error) {
            console.error(error);
            setConnectionState("Live update unavailable · retrying", false);
            setTimeout(start, 5000);
        }
    }

    void start();
})();
