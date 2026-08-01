// Markdown rendering for the chat thread.
//
// Jack's output is model-generated text rendered as HTML, which is exactly the shape of an injection
// bug. Everything goes through DOMPurify — not because Jack is hostile, but because his input includes
// web pages he fetched and files he read, and those are not under anyone's control.
//
// Enhancement happens on the DOM after sanitising rather than through marked's renderer hooks. Those
// hooks changed shape between marked versions — v5 dropped the `highlight` option, v15 passes token
// objects where earlier versions passed strings — and an override written against the wrong one fails
// silently, leaving fenced code and tables on screen as raw pipes and backticks. Walking the output is
// version-independent and obviously correct.

(function () {
    'use strict';

    marked.setOptions({ gfm: true, breaks: true });

    function highlight(element) {
        const classes = element.className || '';
        const declared = /language-([\w+-]+)/.exec(classes);
        const code = element.textContent;

        try {
            if (declared && hljs.getLanguage(declared[1])) {
                return { language: declared[1], value: hljs.highlight(code, { language: declared[1], ignoreIllegals: true }).value };
            }

            const auto = hljs.highlightAuto(code);
            return { language: auto.language || 'text', value: auto.value };
        } catch {
            return { language: declared ? declared[1] : 'text', value: null };
        }
    }

    /** Wraps a code block in a header carrying its language and a copy button. */
    function decorateCode(pre, document) {
        const code = pre.querySelector('code');
        if (!code) return;

        const { language, value } = highlight(code);
        if (value !== null) code.innerHTML = value;
        code.classList.add('hljs');

        const figure = document.createElement('figure');
        figure.className = 'code';

        const caption = document.createElement('figcaption');
        const label = document.createElement('span');
        label.textContent = language;

        // Copying a snippet is the most common thing anyone does with a chat reply, and selecting it
        // by hand in a scrolling panel is miserable.
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'code-copy';
        button.textContent = 'Copy';
        button.dataset.code = encodeURIComponent(code.textContent);

        caption.appendChild(label);
        caption.appendChild(button);

        pre.parentNode.insertBefore(figure, pre);
        figure.appendChild(caption);
        figure.appendChild(pre);
    }

    const api = {
        /** Renders Markdown to sanitised, enhanced HTML. */
        render(text) {
            if (!text) return '';

            try {
                const dirty = marked.parse(text);

                const clean = DOMPurify.sanitize(dirty, {
                    ADD_TAGS: ['figure', 'figcaption', 'video', 'audio', 'source'],
                    ADD_ATTR: ['target', 'rel', 'controls', 'type'],
                });

                // A detached document, so nothing here can run a script or touch the live page.
                const parsed = new DOMParser().parseFromString(
                    `<body>${clean}</body>`, 'text/html');

                parsed.querySelectorAll('pre').forEach((pre) => decorateCode(pre, parsed));

                // Wide tables scroll inside their own box. Without this a wide table forces the whole
                // chat panel to scroll sideways and breaks every message around it.
                parsed.querySelectorAll('table').forEach((table) => {
                    const wrapper = parsed.createElement('div');
                    wrapper.className = 'table-scroll';
                    table.parentNode.insertBefore(wrapper, table);
                    wrapper.appendChild(table);
                });

                parsed.querySelectorAll('a[href]').forEach((anchor) => {
                    if (/^https?:|^mailto:/i.test(anchor.getAttribute('href'))) {
                        anchor.setAttribute('target', '_blank');
                        anchor.setAttribute('rel', 'noopener noreferrer');
                    } else {
                        anchor.removeAttribute('href');
                    }
                });

                return parsed.body.innerHTML;
            } catch (error) {
                // A malformed fragment mid-stream must not blank the message. Showing the raw text is
                // always better than showing nothing.
                const escaped = String(text)
                    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
                return `<pre>${escaped}</pre>`;
            }
        },

        scrollToBottom(selector) {
            const element = document.querySelector(selector);
            if (element) element.scrollTop = element.scrollHeight;
        },

        focus(selector) {
            document.querySelector(selector)?.focus();
        },
    };

    // Bound once at the document level: the thread re-renders on every streamed chunk, and per-button
    // listeners would be attached and thrown away dozens of times a second.
    document.addEventListener('click', (event) => {
        const button = event.target.closest('.code-copy');
        if (!button) return;

        navigator.clipboard.writeText(decodeURIComponent(button.dataset.code || '')).then(() => {
            const original = button.textContent;
            button.textContent = 'Copied';
            button.classList.add('copied');
            setTimeout(() => { button.textContent = original; button.classList.remove('copied'); }, 1400);
        });
    });

    window.unitreeMarkdown = api;
})();
