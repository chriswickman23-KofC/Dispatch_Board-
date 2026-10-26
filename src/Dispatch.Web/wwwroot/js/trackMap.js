let map = null;
let driverMarker = null;
let homeMarker = null;

const pin = (background, symbol) => L.divIcon({
    className: '',
    iconSize: [32, 32],
    iconAnchor: [16, 16],
    html: `<div style="width:32px;height:32px;border-radius:50%;background:${background};border:3px solid #fff;
        box-shadow:0 1px 4px rgba(0,0,0,.4);display:flex;align-items:center;justify-content:center;font-size:15px">${symbol}</div>`,
});

export function init(element, destination, driver) {
    map = L.map(element, { zoomControl: false });
    L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19,
        attribution: '&copy; OpenStreetMap contributors',
    }).addTo(map);
    homeMarker = L.marker([destination.lat, destination.lng], { icon: pin('#16a34a', '🏠') }).addTo(map);
    update(driver);
}

export function update(driver) {
    if (!map) return;
    if (driver) {
        if (!driverMarker) driverMarker = L.marker([driver.lat, driver.lng], { icon: pin('#2563eb', '🚗') }).addTo(map);
        else driverMarker.setLatLng([driver.lat, driver.lng]);
    }
    const points = [homeMarker.getLatLng()];
    if (driverMarker) points.push(driverMarker.getLatLng());
    if (points.length === 1) map.setView(points[0], 15);
    else map.fitBounds(L.latLngBounds(points).pad(0.3));
}

export function dispose() {
    map?.remove();
    map = null;
    driverMarker = null;
    homeMarker = null;
}
