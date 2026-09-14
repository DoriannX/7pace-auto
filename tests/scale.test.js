/* Calcul d’échelle de la vue jour. Le module est chargé tel quel, comme le fait la page :
   c’est le même fichier que celui embarqué dans l’application. */
const assert = require('node:assert/strict');
const { test } = require('node:test');
const scale = require('../src/SeptPaceAuto/web/scale.js');

const BOUNDS = { inner: 600, hours: 10, floor: 36 };

test('les bornes suivent la fenêtre sans jamais dépasser le plafond', () => {
  assert.deepEqual(scale.zoomBounds(BOUNDS), { min: 60, max: 600 });
  // Fenêtre basse : le plancher lisible prend le relais, la surface défilera.
  assert.equal(scale.zoomBounds({ ...BOUNDS, inner: 200 }).min, 36);
  // Écran très haut : le minimum ne peut pas passer devant le maximum.
  assert.equal(scale.zoomBounds({ ...BOUNDS, inner: 12000 }).min, 600);
  assert.equal(scale.zoomBounds({ ...BOUNDS, inner: 0 }).min, 36);
  assert.equal(scale.zoomBounds({ ...BOUNDS, inner: NaN }).min, 36);
});

test('la préférence est bornée à l’affichage, jamais devinée', () => {
  const bounds = { min: 60, max: 600 };
  // Aucune préférence : ajustement à la fenêtre.
  assert.equal(scale.effectiveZoom(0, bounds), 60);
  assert.equal(scale.effectiveZoom(400, bounds), 400);
  // Fenêtre agrandie au-delà du zoom choisi : on affiche l’ajustement, sans rien réécrire.
  assert.equal(scale.effectiveZoom(40, bounds), 60);
  assert.equal(scale.effectiveZoom(900, bounds), 600);
});

test('le geste double l’échelle tous les 150 px et revient sur ses pas', () => {
  assert.equal(scale.zoomFromDrag(100, -150), 200);
  assert.equal(scale.zoomFromDrag(100, -300), 400);
  assert.equal(scale.zoomFromDrag(200, 150), 100);
  assert.equal(scale.zoomFromDrag(100, 0), 100);
  assert.equal(scale.zoomFromDrag(scale.zoomFromDrag(100, -150), 150), 100);
});

test('l’ancre reste sous le curseur, dans les limites du défilement', () => {
  const anchor = { gridOffset: 10, anchorMinutes: 120, viewportY: 200, maxScroll: 6000 };
  // À l’échelle de départ, l’ancre est déjà au-dessus du bord haut : on reste en haut.
  assert.equal(scale.anchoredScrollTop({ ...anchor, hourPx: 60 }), 0);
  // Zoom avant : le contenu défile pour garder l’instant à la même ordonnée écran.
  assert.equal(scale.anchoredScrollTop({ ...anchor, hourPx: 600 }), 1010);
  // Fin de journée : la demande est rognée par la hauteur réellement défilante.
  assert.equal(scale.anchoredScrollTop({ ...anchor, hourPx: 600, maxScroll: 500 }), 500);
  assert.equal(scale.anchoredScrollTop({ ...anchor, anchorMinutes: 0, hourPx: 600 }), 0);
  assert.equal(scale.anchoredScrollTop({ ...anchor, anchorMinutes: 600, hourPx: 600 }), 5810);
});

test('les graduations se densifient en gardant 28 px entre deux étiquettes', () => {
  assert.equal(scale.tickStep(55), 60);
  assert.equal(scale.tickStep(56), 30);
  assert.equal(scale.tickStep(111), 30);
  assert.equal(scale.tickStep(112), 15);
  assert.equal(scale.tickStep(335), 15);
  assert.equal(scale.tickStep(336), 5);
  assert.equal(scale.tickStep(600), 5);
  assert.equal(scale.tickStep(1680), 1);
  for (let px = 36; px <= 600; px += 1) {
    const step = scale.tickStep(px);
    if (step === 60) continue;
    assert.ok(step * px / 60 >= scale.TICK_MIN_GAP, `pas trop serré à ${px} px/h`);
  }
});

test('une valeur mémorisée absente ou aberrante retombe sur l’ajustement', () => {
  for (const raw of [undefined, null, 'abc', 0, -5, NaN, Infinity, -Infinity, {}]) {
    assert.equal(scale.storedZoom(raw), 0, `valeur refusée : ${String(raw)}`);
  }
  assert.equal(scale.storedZoom(120.4), 120);
  assert.equal(scale.storedZoom('240'), 240);
  assert.equal(scale.storedZoom(20), 36);
  assert.equal(scale.storedZoom(5000), 600);
});

test('l’amplitude visible est annoncée par tranches de 5 minutes', () => {
  assert.equal(scale.visibleMinutes(600, 60), 600);
  assert.equal(scale.visibleMinutes(600, 343), 105);
  assert.equal(scale.visibleMinutes(600, 600), 60);
  assert.equal(scale.visibleMinutes(600, 0), 1);
});
