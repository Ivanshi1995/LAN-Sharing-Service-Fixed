(() => {
    'use strict';

    const channel = 'LanFileShare.PreviewStorage';
    const areas = ['localStorage', 'sessionStorage'];
    const maximumSize = 512 * 1024;

    if (window.parent === window) {
        const storageKey = channel + '.' + document.documentElement.dataset.previewScope;

        function read(area) {
            try {
                return new Map(JSON.parse(window[area].getItem(storageKey) || '[]'));
            } catch {
                return null;
            }
        }

        window.addEventListener('message', event => {
            const frame = document.querySelector('iframe');
            const message = event.data;
            if (event.origin !== 'null' || event.source !== frame?.contentWindow || message?.channel !== channel) return;

            if (message.type === 'read') {
                const snapshots = areas.map(area => {
                    const entries = read(area);
                    return entries ? [...entries] : null;
                });
                event.source.postMessage({ channel, id: message.id, snapshots }, '*');
                return;
            }

            const { area, operation, key, value } = message;
            if (message.type !== 'write' || !areas.includes(area)) return;
            if (operation !== 'clear' && (typeof key !== 'string' || key.length > maximumSize)) return;
            if (operation === 'set' && (typeof value !== 'string' || value.length > maximumSize)) return;

            const entries = read(area);
            if (!entries) return;
            switch (operation) {
                case 'set': entries.set(key, value); break;
                case 'remove': entries.delete(key); break;
                case 'clear': entries.clear(); break;
                default: return;
            }
            const serialized = JSON.stringify([...entries]);
            if (serialized.length > maximumSize) return;
            try {
                window[area].setItem(storageKey, serialized);
            } catch {
                // Storage may be disabled or full in the browser.
            }
        });
        return;
    }

    const parentOrigin = location.origin;

    function readSnapshots() {
        const id = Array.from(crypto.getRandomValues(new Uint32Array(4))).join('.');
        return new Promise(resolve => {
            const receive = event => {
                if (event.source !== parent || event.origin !== parentOrigin ||
                    event.data?.channel !== channel || event.data.id !== id) return;
                clearTimeout(timeout);
                window.removeEventListener('message', receive);
                resolve(event.data.snapshots);
            };
            const timeout = setTimeout(() => {
                window.removeEventListener('message', receive);
                resolve([]);
            }, 1500);
            window.addEventListener('message', receive);
            parent.postMessage({ channel, type: 'read', id }, parentOrigin);
        });
    }

    function createStorage(area, snapshot) {
        const entries = new Map(snapshot);
        function write(operation, key, value) {
            parent.postMessage({ channel, type: 'write', area, operation, key, value }, parentOrigin);
        }
        return {
            get length() { return entries.size; },
            key(index) { return [...entries.keys()][Number(index) >>> 0] ?? null; },
            getItem(key) { return entries.get(String(key)) ?? null; },
            setItem(key, value) {
                key = String(key);
                value = String(value);
                const updated = new Map(entries).set(key, value);
                if (JSON.stringify([...updated]).length > maximumSize) {
                    throw new DOMException('Preview storage is full.', 'QuotaExceededError');
                }
                entries.set(key, value);
                write('set', key, value);
            },
            removeItem(key) {
                key = String(key);
                entries.delete(key);
                write('remove', key);
            },
            clear() {
                entries.clear();
                write('clear');
            }
        };
    }

    function decodeHtml(buffer) {
        const bytes = new Uint8Array(buffer);
        const prefix = new TextDecoder('windows-1252').decode(bytes.subarray(0, 1024));
        const charset = prefix.match(/<meta\b[^>]*\bcharset\s*=\s*["']?\s*([\w.-]+)/i)?.[1];
        let encoding = charset || 'utf-8';
        if (bytes[0] === 0xff && bytes[1] === 0xfe) encoding = 'utf-16le';
        else if (bytes[0] === 0xfe && bytes[1] === 0xff) encoding = 'utf-16be';
        else if (bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) encoding = 'utf-8';
        try { return new TextDecoder(encoding).decode(bytes); }
        catch { return new TextDecoder().decode(bytes); }
    }

    async function render() {
        // Load the snapshot before the shared document's first script runs.
        const [snapshots, response] = await Promise.all([
            readSnapshots(),
            fetch(location.href, {
                headers: { Accept: 'application/vnd.lanfileshare.preview+html' },
                credentials: 'omit',
                redirect: 'error'
            })
        ]);
        if (!response.ok) throw new Error('Preview source unavailable.');
        const html = decodeHtml(await response.arrayBuffer());
        document.open();
        areas.forEach((area, index) => {
            if (Array.isArray(snapshots[index])) {
                Object.defineProperty(window, area, { value: createStorage(area, snapshots[index]) });
            }
        });
        document.write(html);
        document.close();
    }

    render().catch(() => {
        document.body.textContent = '网页加载失败。请返回共享页重新打开文件。';
    });
})();
