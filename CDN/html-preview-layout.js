(() => {
    'use strict';

    const viewport = document.querySelector('.preview-viewport');
    const frame = viewport.querySelector('iframe');
    const modes = document.querySelector('.preview-modes');
    const phoneWidth = 393;
    const maxLayoutWidth = 4096;
    let mode = 'auto';
    let availableWidth = 0;
    let fittedWidth = 0;
    let measurement;

    function layout() {
        const width = viewport.clientWidth;
        if (width === 0) return;
        if (availableWidth !== width) {
            availableWidth = width;
            fittedWidth = 0;
        }
        if (mode === 'fit' && measurement?.viewportWidth === frame.clientWidth) {
            fittedWidth = Math.max(fittedWidth, Math.min(maxLayoutWidth, measurement.contentWidth));
        }
        const pageWidth = mode === 'phone' ? Math.min(phoneWidth, width) : Math.max(width, fittedWidth);
        const scale = Math.min(1, width / pageWidth);
        frame.style.width = pageWidth + 'px';
        frame.style.height = Math.floor(viewport.clientHeight / scale) + 'px';
        frame.style.setProperty('--preview-scale', scale);
    }

    modes.addEventListener('click', event => {
        const button = event.target.closest('button[data-mode]');
        if (!button || button.dataset.mode === mode) return;
        mode = button.dataset.mode;
        fittedWidth = 0;
        modes.querySelectorAll('button').forEach(item => item.setAttribute('aria-pressed', String(item === button)));
        layout();
    });

    window.addEventListener('message', event => {
        if (event.source !== frame.contentWindow || event.origin !== 'null' ||
            event.data?.channel !== 'LanFileShare.PreviewLayout') return;
        const { viewportWidth, contentWidth, initial } = event.data;
        if (!Number.isInteger(viewportWidth) || !Number.isInteger(contentWidth) ||
            viewportWidth < 1 || contentWidth < viewportWidth) return;
        measurement = { viewportWidth, contentWidth };
        if (initial) fittedWidth = 0;
        if (mode === 'fit') layout();
    });

    function updateVisibleHeight() {
        const visible = window.visualViewport;
        // Browser zoom stays under the user's control. Adjust for the keyboard only at normal scale.
        if (visible && Math.abs(visible.scale - 1) < 0.01 && visible.height < window.innerHeight - 1) {
            document.body.style.setProperty('--preview-height', Math.floor(visible.height) + 'px');
        } else {
            document.body.style.removeProperty('--preview-height');
        }
        layout();
    }

    new ResizeObserver(layout).observe(viewport);
    window.addEventListener('resize', updateVisibleHeight);
    window.visualViewport?.addEventListener('resize', updateVisibleHeight);
    updateVisibleHeight();
})();
