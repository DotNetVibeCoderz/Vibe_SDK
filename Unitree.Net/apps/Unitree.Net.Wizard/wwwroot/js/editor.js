// Monaco wrapper.
//
// One editor instance is reused across every open document, with a model per file. Creating an editor
// per tab is the obvious approach and the wrong one: each instance carries its own workers and DOM,
// and switching tabs in a project with a dozen files becomes visibly slow.

(function () {
    'use strict';

    let editor = null;
    let dotNet = null;
    const models = new Map();   // path -> monaco.editor.ITextModel
    const viewStates = new Map();
    let currentPath = null;
    let suppressChange = false;

    const THEMES = { dark: 'unitree-dark', light: 'unitree-light' };

    function defineThemes(monaco) {
        // Matched to app.css rather than using vs-dark, so the editor does not sit in the window as a
        // differently-coloured rectangle.
        monaco.editor.defineTheme('unitree-dark', {
            base: 'vs-dark',
            inherit: true,
            rules: [
                { token: 'comment', foreground: '5f6f84', fontStyle: 'italic' },
                { token: 'keyword', foreground: '7ad3e2' },
                { token: 'string', foreground: 'c3d98a' },
                { token: 'number', foreground: 'f2a33c' },
                { token: 'type', foreground: 'a8c7e8' },
            ],
            colors: {
                'editor.background': '#0f1319',
                'editor.foreground': '#e6edf6',
                'editorLineNumber.foreground': '#3f4c5e',
                'editorLineNumber.activeForeground': '#93a3b8',
                'editor.selectionBackground': '#20455c80',
                'editor.lineHighlightBackground': '#161c2480',
                'editorCursor.foreground': '#38cfe0',
                'editorIndentGuide.background1': '#1e2631',
                'editorGutter.background': '#0f1319',
                'editorWidget.background': '#161c24',
                'editorWidget.border': '#2a3543',
                'input.background': '#1e2631',
                'dropdown.background': '#1e2631',
                'scrollbarSlider.background': '#26313080',
            },
        });

        monaco.editor.defineTheme('unitree-light', {
            base: 'vs',
            inherit: true,
            rules: [{ token: 'comment', foreground: '7d8896', fontStyle: 'italic' }],
            colors: {
                'editor.background': '#ffffff',
                'editor.foreground': '#10161d',
                'editorLineNumber.foreground': '#a9b1bb',
                'editorLineNumber.activeForeground': '#4e5a68',
                'editor.lineHighlightBackground': '#f4f3ef',
                'editorCursor.foreground': '#0b7c8a',
            },
        });
    }

    const api = {
        /** Creates the editor inside the given container. Safe to call more than once. */
        init(selector, theme, showLineNumbers, reference) {
            const host = document.querySelector(selector);
            if (!host || editor) return !!editor;

            dotNet = reference;
            defineThemes(monaco);

            editor = monaco.editor.create(host, {
                value: '',
                language: 'csharp',
                theme: THEMES[theme] || THEMES.dark,
                automaticLayout: true,
                lineNumbers: showLineNumbers ? 'on' : 'off',
                fontFamily: '"Cascadia Mono", "Cascadia Code", ui-monospace, Consolas, monospace',
                fontSize: 13,
                lineHeight: 20,
                minimap: { enabled: true, renderCharacters: false, maxColumn: 90 },
                scrollBeyondLastLine: false,
                renderWhitespace: 'selection',
                smoothScrolling: true,
                cursorBlinking: 'smooth',
                tabSize: 4,
                insertSpaces: true,
                bracketPairColorization: { enabled: true },
                padding: { top: 10, bottom: 10 },
            });

            editor.onDidChangeModelContent(() => {
                // Suppressed while switching documents: setting a model raises this too, and without
                // the guard every tab switch would mark the file dirty.
                if (suppressChange || !dotNet) return;
                dotNet.invokeMethodAsync('OnEditorChanged', editor.getValue());
            });

            editor.onDidChangeCursorPosition((e) => {
                if (dotNet) dotNet.invokeMethodAsync('OnCursorMoved', e.position.lineNumber, e.position.column);
            });

            // Ctrl+S has to be bound here rather than on the document: the editor swallows keystrokes
            // before they reach the page.
            editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => {
                if (dotNet) dotNet.invokeMethodAsync('OnSaveRequested');
            });

            return true;
        },

        /** Shows a document, remembering the scroll position and selection of the one being left. */
        open(path, text, language) {
            if (!editor) return;

            if (currentPath) viewStates.set(currentPath, editor.saveViewState());

            let model = models.get(path);

            if (!model) {
                model = monaco.editor.createModel(text, language);
                models.set(path, model);
            } else if (model.getValue() !== text) {
                // Reached when Jack rewrites a file that is already open. pushEditOperations rather
                // than setValue so the undo stack survives.
                model.pushEditOperations(
                    [], [{ range: model.getFullModelRange(), text }], () => null);
            }

            suppressChange = true;
            editor.setModel(model);
            suppressChange = false;

            const state = viewStates.get(path);
            if (state) editor.restoreViewState(state);

            currentPath = path;
            editor.focus();
        },

        /** Forgets a document's model and view state. */
        close(path) {
            const model = models.get(path);
            if (model) { model.dispose(); models.delete(path); }
            viewStates.delete(path);
            if (currentPath === path) currentPath = null;
        },

        setTheme(theme) {
            if (editor) monaco.editor.setTheme(THEMES[theme] || THEMES.dark);
            const link = document.getElementById('hljs-theme');
            if (link) link.href = `lib/highlight/github${theme === 'light' ? '' : '-dark'}.min.css`;
        },

        setLineNumbers(show) {
            if (editor) editor.updateOptions({ lineNumbers: show ? 'on' : 'off' });
        },

        // Commands the Edit menu drives. Monaco owns the clipboard and the find widget, so the menu
        // asks the editor to run its own action rather than reimplementing any of it.
        action(id) { if (editor) { editor.focus(); editor.trigger('menu', id, null); } },
        undo() { api.action('undo'); },
        redo() { api.action('redo'); },
        cut() { api.action('editor.action.clipboardCutAction'); },
        copy() { api.action('editor.action.clipboardCopyAction'); },
        paste() { api.action('editor.action.clipboardPasteAction'); },
        selectAll() { api.action('editor.action.selectAll'); },
        find() { api.action('actions.find'); },
        replace() { api.action('editor.action.startFindReplaceAction'); },
        gotoLine() { api.action('editor.action.gotoLine'); },
        format() { api.action('editor.action.formatDocument'); },
        commandPalette() { api.action('editor.action.quickCommand'); },

        goToLineNumber(line) {
            if (!editor) return;
            editor.revealLineInCenter(line);
            editor.setPosition({ lineNumber: line, column: 1 });
            editor.focus();
        },

        getValue() { return editor ? editor.getValue() : ''; },

        focus() { if (editor) editor.focus(); },
    };

    require.config({ paths: { vs: 'lib/monaco/vs' } });

    require(['vs/editor/editor.main'], function () {
        window.unitreeEditor = api;
        window.dispatchEvent(new Event('unitree:editor-ready'));
    });
})();
