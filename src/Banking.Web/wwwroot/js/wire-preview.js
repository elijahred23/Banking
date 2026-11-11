(() => {
    // Delegation also covers forms replaced by live wire-detail updates.
    document.addEventListener('click', async event => {
        const button = event.target.closest('[data-message-preview]');
        if (!button || button.disabled) return;
        const form = button.closest('form');
        if (!form || !form.reportValidity()) return;

        const dialog = document.createElement('dialog');
        dialog.className = 'wire-preview-dialog surface';
        const heading = document.createElement('h2');
        heading.className = 'h4';
        heading.id = 'wire-preview-heading';
        dialog.setAttribute('aria-labelledby', heading.id);
        heading.textContent = 'Generated message preview';
        const note = document.createElement('p');
        note.className = 'text-secondary';
        note.textContent = 'Learning simulator preview only. Nothing is saved or sent. IDs and timestamps are samples and may change when submitted. A return payment is conditional on acceptance and available funds.';
        const content = document.createElement('div');
        content.setAttribute('role', 'status');
        content.textContent = 'Generating preview…';
        const close = document.createElement('button');
        close.type = 'button';
        close.className = 'btn btn-secondary mt-3';
        close.textContent = 'Close preview';
        close.addEventListener('click', () => dialog.close());
        dialog.append(heading, note, content, close);
        document.body.append(dialog);
        dialog.addEventListener('close', () => {
            dialog.remove();
            button.focus();
        });
        dialog.showModal();
        button.disabled = true;
        try {
            const data = new FormData(form);
            data.set('preview', 'true');
            if (button.dataset.caseType) data.set('type', button.dataset.caseType);
            const response = await fetch(form.action, {
                method: 'POST', body: data, headers: { Accept: 'application/json' }
            });
            if (!response.headers.get('content-type')?.includes('application/json'))
                throw new Error('Preview unavailable. Check the form values and your session, then try again.');
            const result = await response.json();
            if (!response.ok)
                throw new Error(result.errors?.filter(Boolean).join(' ') || result.error || 'Unable to preview this message.');
            content.removeAttribute('role');
            content.replaceChildren();
            for (const message of result.messages) {
                const title = document.createElement('h3');
                title.className = 'h6 mb-0';
                title.textContent = message.messageType + ' · ' + message.direction;
                const toolbar = document.createElement('div');
                toolbar.className = 'd-flex flex-wrap align-items-center justify-content-between gap-3 mt-3 mb-2';
                const copy = document.createElement('button');
                copy.type = 'button';
                copy.className = 'btn btn-outline-secondary btn-sm';
                copy.textContent = 'Copy XML';
                copy.setAttribute('aria-label', 'Copy ' + message.messageType + ' XML');
                const copyStatus = document.createElement('span');
                copyStatus.className = 'small text-secondary';
                copyStatus.setAttribute('role', 'status');
                copy.addEventListener('click', async () => {
                    try {
                        if (navigator.clipboard?.writeText) {
                            await navigator.clipboard.writeText(message.xml);
                        } else {
                            const text = document.createElement('textarea');
                            text.value = message.xml;
                            text.setAttribute('aria-label', 'XML to copy');
                            text.style.position = 'fixed';
                            text.style.opacity = '0';
                            dialog.append(text);
                            try {
                                text.select();
                                if (!document.execCommand('copy'))
                                    throw new Error('Clipboard unavailable');
                            } finally {
                                text.remove();
                                copy.focus();
                            }
                        }
                        copyStatus.textContent = 'XML copied.';
                    } catch {
                        copyStatus.textContent = 'Could not copy. Select the XML and copy it manually.';
                    }
                });
                toolbar.append(title, copy, copyStatus);
                const pre = document.createElement('pre');
                pre.className = 'xml-viewer';
                const code = document.createElement('code');
                code.textContent = message.xml;
                pre.append(code);
                content.append(toolbar, pre);
            }
            window.bankingXmlViewer.highlight(content);
            if (result.visualization)
                window.bankingWireVisualization.mount(content, result.visualization, form, result.messages);
        } catch (error) {
            content.setAttribute('role', 'alert');
            content.textContent = error.message || 'Unable to preview this message.';
        } finally {
            button.disabled = false;
        }
    });
})();
