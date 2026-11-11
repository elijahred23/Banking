(() => {
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

    function highlightXml(root) {
        root.querySelectorAll(".xml-viewer code").forEach(code => {
            const xml = code.dataset.rawXml ?? code.textContent;
            code.dataset.rawXml = xml;
            code.innerHTML = xml
                .split(/(<!--[\s\S]*?-->|<!\[CDATA\[[\s\S]*?\]\]>|<(?:"[^"]*"|'[^']*'|[^'">])*>)/g)
                .map(part => part.startsWith("<") ? highlightXmlTag(part) : escapeHtml(part))
                .join("");
        });
    }

    window.bankingXmlViewer = { highlight: highlightXml };
})();
