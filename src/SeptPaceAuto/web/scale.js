/* Échelle de la vue jour : tout le calcul du zoom, sans DOM.
   Ce fichier est chargé comme script classique avant app.js — il publie DayScale sur
   window — et se laisse aussi charger par Node pour les tests (module.exports). Rien
   ici ne lit ni n’écrit la page : les seules entrées sont des nombres mesurés par
   l’appelant, les seules sorties des nombres à écrire dans les variables CSS ou dans
   scrollTop. C’est ce qui rend le geste vérifiable sans navigateur. */
const DayScale = {
  /* Plafond du zoom : 600 px par heure, soit 10 px par minute — un créneau de deux
     minutes reste lisible, et une journée entière ne dépasse pas 6000 px. */
  ZOOM_MAX: 600,
  /* Plancher absolu, celui de la vue jour non zoomée. */
  ZOOM_FLOOR: 36,
  /* Loi du geste : l’échelle double tous les 150 px de glissement vertical. */
  ZOOM_DOUBLING_PX: 150,
  /* Espacement minimal entre deux graduations étiquetées. */
  TICK_MIN_GAP: 28,
  /* Pas candidats, du plus large au plus fin. */
  TICK_STEPS: [60, 30, 15, 5, 1],

  /* Bornes courantes : le minimum est l’ajustement à la fenêtre — jamais de vide sous
     18:00 — et ne dépasse jamais le plafond sur un écran très haut. */
  zoomBounds({ inner, hours, floor }) {
    const max = DayScale.ZOOM_MAX;
    if (!Number.isFinite(inner) || inner <= 0 || !Number.isFinite(hours) || hours <= 0) {
      return { min: floor, max };
    }
    return { min: Math.min(max, Math.max(floor, Math.floor(inner / hours))), max };
  },

  /* Préférence relue de l’hôte. 0 signifie « aucun zoom mémorisé », donc ajustement à la
     fenêtre : une valeur absente ou aberrante retombe silencieusement sur le défaut. */
  storedZoom(raw) {
    const value = typeof raw === 'number' ? raw : Number(raw);
    if (!Number.isFinite(value) || value <= 0) return 0;
    return Math.min(DayScale.ZOOM_MAX, Math.max(DayScale.ZOOM_FLOOR, Math.round(value)));
  },

  /* Échelle réellement affichée : la préférence est bornée à l’affichage, jamais réécrite. */
  effectiveZoom(stored, bounds) {
    if (!(stored > 0)) return bounds.min;
    return Math.min(Math.max(stored, bounds.min), bounds.max);
  },

  /* Glissement vers le haut (dy négatif) = zoom avant, loi géométrique et continue. */
  zoomFromDrag(startPx, dy) {
    return Math.round(startPx * Math.pow(2, -dy / DayScale.ZOOM_DOUBLING_PX));
  },

  /* Ancrage : l’instant saisi au début du geste reste sous le curseur. anchorMinutes est
     sa position sur l’axe, viewportY l’ordonnée écran à laquelle il doit rester. */
  anchoredScrollTop({ gridOffset, anchorMinutes, viewportY, hourPx, maxScroll }) {
    const wanted = gridOffset + anchorMinutes * hourPx / 60 - viewportY;
    return Math.min(Math.max(wanted, 0), Math.max(0, maxScroll));
  },

  /* Graduations : le pas le plus fin dont l’espacement tient l’écart minimal. Les pas
     sont parcourus du plus fin au plus large, sinon 60 min gagnerait toujours. */
  tickStep(hourPx) {
    for (let index = DayScale.TICK_STEPS.length - 1; index >= 0; index -= 1) {
      const step = DayScale.TICK_STEPS[index];
      if (step * hourPx / 60 >= DayScale.TICK_MIN_GAP) return step;
    }
    return 60;
  },

  /* Amplitude visible, arrondie à 5 minutes pour que l’indicateur ne clignote pas. */
  visibleMinutes(clientHeight, hourPx) {
    if (!Number.isFinite(clientHeight) || !Number.isFinite(hourPx) || hourPx <= 0) return 1;
    return Math.max(1, Math.round(clientHeight / hourPx * 60 / 5) * 5);
  }
};

if (typeof window !== 'undefined') window.DayScale = DayScale;
if (typeof module !== 'undefined' && module.exports) module.exports = DayScale;
