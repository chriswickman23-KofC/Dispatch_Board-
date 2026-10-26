const STATUS_COLOR = { available: '#16a34a', busy: '#2563eb', offline: '#94a3b8' };
const STOP_BG = { pending: '#0f172a', arrived: '#2563eb', completed: '#16a34a', failed: '#dc2626' };

let map = null;
let dotnet = null;
let overlay = null;
let trail = null;
let planned = null;
const driverMarkers = new Map();
const iconCache = new Map();

const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const popupHtml = (lines) => (lines ?? []).map((l, i) => (i === 0 ? `<b>${esc(l)}</b>` : esc(l))).join('<br>');
const cached = (key, make) => {
    if (!iconCache.has(key)) iconCache.set(key, make());
    return iconCache.get(key);
};

const driverIcon = (d) => cached(`d|${d.status}|${d.stale}|${d.selected}|${d.initials}`, () => L.divIcon({
    className: '', iconSize: [32, 32], iconAnchor: [16, 16],
    html: `<div style="width:32px;height:32px;border-radius:50%;background:${STATUS_COLOR[d.status]};color:#fff;
        font:600 11px system-ui;display:flex;align-items:center;justify-content:center;
        border:3px ${d.stale ? 'dashed #dc2626' : 'solid #fff'};
        box-shadow:0 0 0 ${d.selected ? 4 : 0}px rgba(37,99,235,.5),0 1px 4px rgba(0,0,0,.4)">${esc(d.initials)}</div>`,
}));

const jobIcon = (color, selected) => cached(`j|${color}|${selected}`, () => L.divIcon({
    className: '', iconSize: [18, 18], iconAnchor: [9, 9],
    html: `<div style="width:16px;height:16px;background:${color};transform:rotate(45deg);border:2px solid #fff;
        box-shadow:0 0 0 ${selected ? 3 : 0}px #2563eb,0 1px 3px rgba(0,0,0,.4)"></div>`,
}));

const stopIcon = (label, status) => cached(`s|${label}|${status}`, () => L.divIcon({
    className: '', iconSize: [22, 22], iconAnchor: [11, 11],
    html: `<div style="width:22px;height:22px;border-radius:50%;background:${STOP_BG[status]};color:#fff;
        font:600 11px system-ui;display:flex;align-items:center;justify-content:center;border:2px solid #fff">${esc(label)}</div>`,
}));

export function init(element, dotnetRef, center) {
    dotnet = dotnetRef;
    map = L.map(element).setView(center, 13);
    L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19,
        attribution: '&copy; OpenStreetMap contributors',
    }).addTo(map);
    overlay = L.layerGroup().addTo(map);
    trail = L.polyline([], { color: '#64748b', weight: 3, opacity: 0.6 }).addTo(map);
    planned = L.polyline([], { color: '#2563eb', weight: 4, dashArray: '8 6' }).addTo(map);
    setTimeout(() => map && map.invalidateSize(), 100);
}

export function setDrivers(drivers) {
    if (!map) return;
    const seen = new Set();
    for (const d of drivers) {
        seen.add(d.id);
        const icon = driverIcon(d);
        let marker = driverMarkers.get(d.id);
        if (!marker) {
            marker = L.marker([d.lat, d.lng], { icon }).addTo(map);
            marker.on('click', () => dotnet.invokeMethodAsync('OnDriverClicked', d.id));
            marker.bindPopup(popupHtml(d.popup));
            driverMarkers.set(d.id, marker);
        } else {
            marker.setLatLng([d.lat, d.lng]);
            if (marker.options.icon !== icon) marker.setIcon(icon);
            marker.setPopupContent(popupHtml(d.popup));
        }
        marker.setZIndexOffset(d.selected ? 1000 : 500);
    }
    for (const [id, marker] of driverMarkers) {
        if (!seen.has(id)) {
            marker.remove();
            driverMarkers.delete(id);
        }
    }
}

export function setOverlay(stops, jobs) {
    if (!map) return;
    overlay.clearLayers();
    for (const s of stops) {
        L.marker([s.lat, s.lng], { icon: stopIcon(s.label, s.status) })
            .bindPopup(popupHtml(s.popup))
            .addTo(overlay);
    }
    for (const j of jobs) {
        const marker = L.marker([j.lat, j.lng], { icon: jobIcon(j.color, j.selected) })
            .bindPopup(popupHtml(j.popup))
            .addTo(overlay);
        marker.on('click', () => dotnet.invokeMethodAsync('OnJobClicked', j.id));
    }
}

export function setLines(trailPoints, plannedPoints) {
    if (!map) return;
    trail.setLatLngs(trailPoints ?? []);
    planned.setLatLngs(plannedPoints ?? []);
}

export function flyTo(lat, lng) {
    map?.flyTo([lat, lng], 15, { duration: 0.8 });
}

/** Pans only when the target is off-screen, so the map doesn't jerk around while someone's watching. */
export function panToIfHidden(lat, lng) {
    if (map && !map.getBounds().pad(-0.1).contains([lat, lng])) map.panTo([lat, lng]);
}

export function dispose() {
    map?.remove();
    map = null;
    driverMarkers.clear();
}
