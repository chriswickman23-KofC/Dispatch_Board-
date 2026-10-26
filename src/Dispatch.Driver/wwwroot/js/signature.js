export function init(canvas) {
    const ctx = canvas.getContext('2d');
    const state = { drawing: false, empty: true };
    canvas._sig = state;
    fill(canvas);

    const point = (e) => {
        const r = canvas.getBoundingClientRect();
        return [(e.clientX - r.left) * (canvas.width / r.width), (e.clientY - r.top) * (canvas.height / r.height)];
    };

    canvas.addEventListener('pointerdown', (e) => {
        state.drawing = true;
        state.empty = false;
        const [x, y] = point(e);
        ctx.beginPath();
        ctx.moveTo(x, y);
        canvas.setPointerCapture(e.pointerId);
    });
    canvas.addEventListener('pointermove', (e) => {
        if (!state.drawing) return;
        const [x, y] = point(e);
        ctx.lineTo(x, y);
        ctx.stroke();
    });
    const end = () => { state.drawing = false; };
    canvas.addEventListener('pointerup', end);
    canvas.addEventListener('pointercancel', end);
}

function fill(canvas) {
    const ctx = canvas.getContext('2d');
    ctx.fillStyle = '#fff';
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.lineWidth = 4;
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';
    ctx.strokeStyle = '#0f172a';
}

export function clear(canvas) {
    fill(canvas);
    if (canvas._sig) canvas._sig.empty = true;
}

export const isEmpty = (canvas) => canvas._sig?.empty ?? true;
export const toPng = (canvas) => canvas.toDataURL('image/png');
