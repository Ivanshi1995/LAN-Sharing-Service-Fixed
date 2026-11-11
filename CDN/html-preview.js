function createHtmlPreview(frame, loading, showError) {
    let pending = null;

    function close() {
        pending?.abort();
        pending = null;
        frame.classList.add('hidden');
        frame.removeAttribute('src');
    }

    async function open(url) {
        const request = new AbortController();
        pending = request;
        try {
            const response = await fetch(url, { signal: request.signal });
            if (!response.ok) throw new Error();
            const preview = await response.json();
            if (pending !== request) return;
            if (typeof preview.url !== 'string' || !preview.url.startsWith('/api/html-preview/')) throw new Error();
            frame.src = preview.url;
            frame.classList.remove('hidden');
            loading.classList.add('hidden');
        } catch (_) {
            if (pending === request) showError('网页预览加载失败。请重新打开文件。');
        } finally {
            if (pending === request) pending = null;
        }
    }

    return { open, close };
}
