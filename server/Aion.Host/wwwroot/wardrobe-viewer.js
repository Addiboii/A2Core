// Character preview shared by the research wardrobe (tools/wardrobe.html) and the staff
// dashboard (admin.html). Both servers expose the wardrobe export at /wardrobe/files/
// and its index at /wardrobe/data; three.js is vendored there by `tools/wardrobe.py vendor`.
// Meshes are extracted AION2 assets with approximate materials; see research/wardrobe.md.
import * as THREE from '/wardrobe/files/vendor/three/three.module.js';
import {OrbitControls} from '/wardrobe/files/vendor/three/OrbitControls.js';
import {GLTFLoader} from '/wardrobe/files/vendor/three/GLTFLoader.js';

export const FILES = '/wardrobe/files/';
export const SLOTS = ['Helmet', 'Shoulder', 'Body', 'Glove', 'Pants', 'Boots', 'Cape', 'Weapon', 'Necklace', 'Earring'];
export const SLOT_LABEL = {Body: 'Torso', Glove: 'Gloves'};
// Slots with a base-body replacement when empty.
const BASE_FOR = {Body: 'Body', Pants: 'Pants', Boots: 'Boots'};
// Library textures (checker patterns, flat masks) would only obscure the item's own maps.
const GENERIC = /textures\/Material\/|T_Mask_Black|T_Checker|T_Base_M\b/;
// Root-skeleton attach bone per weapon type (from the /Weapon/<type>/ path). Swords have
// no bone of their own here, so they use the one-handed mace grip. The orb idle animates
// ORWeapon01 and MbookWeapon as the two floating orbs. Daggers are shown in both hands.
const WEAPON_BONES = {GS: ['GswordWeapon'], SW: ['MaceWeapon'], MA: ['MaceWeapon'], DA: ['DaggerWeapon', 'DaggerWeapon_Sub'],
                      BW: ['BowWeapon'], ST: ['StaffWeapon'], MB: ['MbookWeapon'], OR_L: ['MbookWeapon'], OR_R: ['ORWeapon01']};
const SKIN = new THREE.Color(0.80, 0.56, 0.45);

export async function json(path, options) {
  const response = await fetch(path, options);
  if (!response.ok) throw Error(`${response.status} ${path}`);
  return response.json();
}

export function wearable(item) { return Boolean(item.slot && (item.parts.GF?.length || item.parts.GM?.length)); }

// stage: element to render into. data: /wardrobe/data. hooks: onStatus(text), onError(text), onChange().
export function createViewer(stage, data, {onStatus = () => {}, onError = () => {}, onChange = () => {}} = {}) {
  let gender = 'GF', attach = {}, poseMode = 'still', hairUnderHelmet = false, loadToken = 0;
  // Decoded stored customization (admin character detail .appearance), or null for stock parts.
  let look = null;
  const equipped = {};           // slot -> item record
  const loadedParts = {};        // slot -> [Object3D]
  let baseParts = {};            // Head/Hair/Body/Pants/Boots -> Object3D

  // ---------- Scene ----------
  const renderer = new THREE.WebGLRenderer({antialias: true, alpha: true});
  renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  stage.prepend(renderer.domElement);
  const scene = new THREE.Scene();
  const camera = new THREE.PerspectiveCamera(30, 1, 0.01, 100);
  const controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true;
  scene.add(new THREE.HemisphereLight(0xdfe8ff, 0x2a2018, 1.4));
  const key = new THREE.DirectionalLight(0xfff1e0, 2.4); key.position.set(2, 4, 3); scene.add(key);
  const rim = new THREE.DirectionalLight(0x9cc4ff, 1.6); rim.position.set(-3, 2, -3); scene.add(rim);
  const floor = new THREE.Mesh(new THREE.CircleGeometry(0.55, 48), new THREE.MeshStandardMaterial({color: 0x1b2230, roughness: 0.9}));
  floor.rotation.x = -Math.PI / 2; scene.add(floor);
  const character = new THREE.Group(); scene.add(character);
  // Re-frame on resize until the user moves the camera themselves.
  let userMoved = false;
  controls.addEventListener('start', () => { userMoved = true; });
  new ResizeObserver(() => {
    const {clientWidth: w, clientHeight: h} = stage;
    renderer.setSize(w, h, false); camera.aspect = w / Math.max(h, 1); camera.updateProjectionMatrix();
    if (!userMoved) frame();
  }).observe(stage);

  // ---------- Asset loading ----------
  const gltfLoader = new GLTFLoader(), textureLoader = new THREE.TextureLoader();
  const textures = new Map(), manifests = new Map();
  function texture(path, srgb) {
    const keyName = path + (srgb ? '#srgb' : '');
    if (!textures.has(keyName)) {
      const t = textureLoader.load(FILES + path);
      t.flipY = false; t.colorSpace = srgb ? THREE.SRGBColorSpace : THREE.NoColorSpace;
      t.wrapS = t.wrapT = THREE.RepeatWrapping; t.anisotropy = 4;
      textures.set(keyName, t);
    }
    return textures.get(keyName);
  }
  function manifest(path) {
    if (!manifests.has(path)) manifests.set(path, json(FILES + path));
    return manifests.get(path);
  }
  const linear = c => new THREE.Color().setRGB(c[0], c[1], c[2], THREE.LinearSRGBColorSpace);

  function buildMaterial(name, info) {
    const m = new THREE.MeshStandardMaterial({name, roughness: 0.6, metalness: 0, side: THREE.DoubleSide});
    if (!info) { m.color.set(0x9a8c80); return m; }
    const t = info.textures || {}, scalars = info.scalars || {}, colors = info.colors || {};
    const pick = (keys, srgb) => { for (const k of keys) if (t[k] && !GENERIC.test(t[k])) return texture(t[k], srgb); return null; };
    const isHair = /Hair/.test(info.parent || '') || 'DIRO' in t;
    const isHead = /Head/.test(info.parent || '') && !/Eye/.test(info.parent || '');
    if (isHair) {
      m.color = linear(colors.MidColor || [0.12, 0.06, 0.03]);
      m.userData.hair = true;
      m.alphaMap = pick(['DIRO'], false); m.alphaTest = 0.3; m.roughness = 0.45;
      // DIRO alpha holds strand opacity; the game shader amplifies it (OpacityFar/Near), so boost it here.
      m.onBeforeCompile = s => { s.fragmentShader = s.fragmentShader.replace('#include <alphamap_fragment>', 'diffuseColor.a *= clamp(texture2D(alphaMap, vAlphaMapUv).a * 2.5, 0.0, 1.0);'); };
      m.customProgramCacheKey = () => 'hair';
      return m;
    }
    if (/Eye/.test(info.parent || '')) {
      if (/AO|Tear/.test(info.parent)) { m.visible = false; return m; }
      m.color = linear(colors.IrisColor_Mid || [0.15, 0.23, 0.26]); m.roughness = 0.15; m.userData.eye = true; return m;
    }
    m.userData.skin = isHead || /BaseBody|Basebody|CM_Body_Cstm/.test(`${info.parent || ''} ${name || ''}`);
    m.map = pick(['BaseColor', 'PM_Diffuse', 'Diffuse'], true);
    if (!m.map && isHead) m.color.copy(SKIN);
    // Skin palette entries tint the underlying complexion. Preserve the warm
    // head fallback when its layered diffuse is unavailable instead of replacing
    // it with a grey palette entry. Retain this baseline for repeated updates.
    if (m.userData.skin) m.userData.skinBaseColor = m.color.clone();
    const normal = pick(['NormalBase', 'Normal', 'PM_Normals'], false);
    if (normal) { m.normalMap = normal; m.normalScale.set(1, -1); }   // DirectX-style green channel
    const arm = pick(['ARM'], false);
    if (arm) { m.roughnessMap = m.metalnessMap = arm; m.roughness = 1; m.metalness = 1; }  // G = roughness, B = metallic
    if (info.blend === 'BLEND_Masked' && m.map) m.alphaTest = 0.333;
    if (info.blend === 'BLEND_Translucent') { m.transparent = true; m.opacity = 0.6; }
    // Approximate the custom-colour masks: each enabled CSTM channel multiplies its default colour.
    const cstm = pick(['Cstm'], false);
    const channels = 'RGBA'.split('').map(c => (scalars['CustomMask' + c + '_Use'] > 0 && colors['CustomMask' + c + '_Color']) ? linear(colors['CustomMask' + c + '_Color']) : null);
    if (m.map && cstm && scalars.Use_ColorCustom !== 0 && channels.some(Boolean)) {
      const uniforms = {cstmMap: {value: cstm}, cstmColors: {value: channels.map(c => c || new THREE.Color(1, 1, 1))},
                        cstmUse: {value: new THREE.Vector4(...channels.map(c => c ? 1 : 0))}};
      m.onBeforeCompile = s => {
        Object.assign(s.uniforms, uniforms);
        s.fragmentShader = 'uniform sampler2D cstmMap; uniform vec3 cstmColors[4]; uniform vec4 cstmUse;\n' + s.fragmentShader.replace('#include <map_fragment>', `#include <map_fragment>
          vec4 mask = texture2D(cstmMap, vMapUv) * cstmUse; vec3 tint = vec3(1.0);
          for (int i = 0; i < 4; i++) tint = mix(tint, tint * cstmColors[i], mask[i]);
          diffuseColor.rgb *= tint;`);
      };
      m.customProgramCacheKey = () => 'cstm';
    }
    return m;
  }

  async function loadPart(path) {
    const info = await manifest(path);
    const gltf = await gltfLoader.loadAsync(FILES + info.glb);
    const byName = Object.fromEntries((info.materials || []).filter(x => x.name).map(x => [x.name, x]));
    gltf.scene.traverse(o => {
      if (!o.isMesh) return;
      o.frustumCulled = false;   // skinned bounds come from the bind pose only
      const convert = mat => buildMaterial(mat.name, byName[mat.name]);
      o.material = Array.isArray(o.material) ? o.material.map(convert) : convert(o.material);
    });
    gltf.scene.userData.path = path;
    return gltf.scene;
  }

  function dispose(object) {
    object?.removeFromParent();
    object?.traverse(o => { if (o.isMesh) [o.material].flat().forEach(m => m.dispose()); o.geometry?.dispose(); });
  }

  // ---------- Character assembly ----------
  // Bones come from the visible torso: the equipped one, else the base body.
  function bone(name) {
    const body = loadedParts.Body?.[0] || baseParts.Body;
    return body?.getObjectByName(name) || null;
  }
  // Recreate a root-skeleton attach bone under the matching bone of the visible body.
  function attachPoint(name) {
    const existing = bone(name);
    if (existing) return existing;
    const record = attach[name];
    const parent = record && (bone(record.bone) || attachPoint(record.bone));
    if (!parent) return null;
    const node = new THREE.Object3D();
    node.name = name; node.position.fromArray(record.translation);
    node.quaternion.fromArray(record.rotation); node.scale.fromArray(record.scale);
    parent.add(node);
    return node;
  }
  function attachWeapons() {
    for (const part of loadedParts.Weapon || []) {
      const type = (part.userData.path.match(/\/Weapon\/(\w+)\//) || [])[1];
      const side = (part.userData.path.match(/_([LR])\.json$/) || [])[1];
      const names = WEAPON_BONES[side ? `${type}_${side}` : type] || ['GswordWeapon'];
      const point = attachPoint(names[part.userData.copy || 0]) || bone('Bip001-R-Hand');
      (point || character).add(part);
    }
  }

  // ---------- Pose ----------
  // The game's lobby idle for the held weapon type (NoWeapon without one), applied by bone
  // name to every part's skeleton and to the weapon attach nodes. Shown as its first frame
  // (the looping playback was glitchy), or switched off for the bind pose.
  const poses = new Map();
  let pose = null, bindings = [];
  const qa = new THREE.Quaternion(), qb = new THREE.Quaternion(), va = new THREE.Vector3();
  function poseType() {
    const path = equipped.Weapon?.parts[gender]?.[0] || '';
    return (path.match(/\/Weapon\/(\w+)\//) || [])[1] || 'NoWeapon';
  }
  async function updatePose() {
    const available = data.poses?.[gender] || {};
    const path = poseMode !== 'bind' ? available[poseType()] || available.NoWeapon : null;
    if (path && !poses.has(path)) poses.set(path, json(FILES + path));
    const next = path ? await poses.get(path) : null;
    pose = next;
    bindPose();
    setFrame(0);
  }
  // Rotations come from the animation; translations only for the root (Bip001) and the
  // attach bones, so each armour skeleton keeps its own proportions. Weapon meshes keep
  // their own skeletons untouched; bones without a track return to their bind pose.
  function bindPose() {
    bindings = [];
    const visit = node => {
      if (node.userData.weapon) return;
      if (node.isBone || node.name in attach) {
        node.userData.bind ??= {q: node.quaternion.clone(), p: node.position.clone()};
        const track = pose?.bones[node.name];
        if (track) bindings.push({node, track, moves: node.name === 'Bip001' || node.name in attach});
        else { node.quaternion.copy(node.userData.bind.q); node.position.copy(node.userData.bind.p); }
      }
      node.children.forEach(visit);
    };
    visit(character);
  }
  function setFrame(seconds) {
    if (!pose) return;
    const f = (seconds * pose.fps) % pose.frames, i = Math.floor(f), j = (i + 1) % pose.frames, t = f - i;
    for (const {node, track, moves} of bindings) {
      const r = track.rotation;
      if (r.length === 4) node.quaternion.fromArray(r);
      else node.quaternion.slerpQuaternions(qa.fromArray(r, i * 4), qb.fromArray(r, j * 4), t);
      const p = track.translation;
      if (!moves) node.position.copy(node.userData.bind.p);
      else if (p.length === 3) node.position.fromArray(p);
      else node.position.fromArray(p, i * 3).lerp(va.fromArray(p, j * 3), t);
    }
  }
  renderer.setAnimationLoop(() => { controls.update(); renderer.render(scene, camera); });

  function updateVisibility() {
    for (const [slot, base] of Object.entries(BASE_FOR)) if (baseParts[base]) baseParts[base].visible = !loadedParts[slot]?.length;
    if (baseParts.Hair) baseParts.Hair.visible = !loadedParts.Helmet?.length || hairUnderHelmet;
  }
  // Stock base parts, with the head and hair the stored customization names when exported
  // (wardrobe.json customize: HairType/HeadType index -> part; SkinColor index -> colour).
  function lookParts() {
    const parts = {...data.base[gender]}, options = data.customize?.[gender];
    if (!look || !options) return parts;
    parts.Head = options.head?.[look.headType] || parts.Head;
    parts.Hair = options.hair?.[look.hairType] || parts.Hair;
    return parts;
  }
  function shownParts() {
    return lookParts();
  }
  // Customization colours (C_<name>_R/G/B, 0-255). They read as linear values: Elyra's brown
  // hair is stored as 13,5,0, which as sRGB is almost black. Null when the fields are absent.
  function lookColor(name) {
    const f = look?.fields || {}, r = f[`C_${name}_R`], g = f[`C_${name}_G`], b = f[`C_${name}_B`];
    return r === undefined || g === undefined || b === undefined ? null
      : new THREE.Color().setRGB(r / 255, g / 255, b / 255, THREE.LinearSRGBColorSpace);
  }
  function applyLookColors() {
    if (!look) return;
    const skin = data.customize?.[gender]?.skin?.[look.skinColor];
    // The eye mesh shows the iris (the pupil colour is the black centre).
    const hair = lookColor('HairColor'), iris = lookColor('IrisColorMid_L');
    [...Object.values(baseParts), ...Object.values(loadedParts).flat()].forEach(part => part.traverse(o => o.isMesh && [o.material].flat().forEach(m => {
      if (m.userData.hair && hair) m.color.copy(hair);
      else if (m.userData.eye && iris) m.color.copy(iris);
      else if (m.userData.skin && skin) m.color.copy(m.userData.skinBaseColor).multiply(new THREE.Color(skin));
    })));
  }
  function hairMaterials() {
    const found = [];
    baseParts.Hair?.traverse(o => o.isMesh && [o.material].flat().forEach(m => m.userData.hair && found.push(m)));
    return found;
  }

  async function loadBase() {
    const token = ++loadToken;
    onStatus('Loading character…');
    Object.values(loadedParts).flat().forEach(dispose);
    for (const slot in loadedParts) delete loadedParts[slot];
    Object.values(baseParts).forEach(dispose); baseParts = {};
    attach = data.attach?.[gender] ? Object.fromEntries((await json(FILES + data.attach[gender])).bones.map(b => [b.name, b])) : {};
    const entries = Object.entries(shownParts()).filter(([, path]) => path);
    const parts = await Promise.all(entries.map(([, path]) => loadPart(path)));
    if (token !== loadToken) { parts.forEach(dispose); return; }
    entries.forEach(([name], i) => { baseParts[name] = parts[i]; character.add(parts[i]); });
    applyLookColors();
    await updatePose();
    userMoved = false; frame(true);
    await Promise.all(SLOTS.filter(s => equipped[s]).map(s => loadSlot(s)));
    if (token === loadToken && !userMoved) frame(true);   // include the saved equipment
    onChange();
    onStatus('');
  }

  async function loadSlot(slot) {
    const item = equipped[slot], token = loadToken;
    // Weapons hang off the torso's bones; keep them out of the torso being replaced.
    if (slot === 'Body') (loadedParts.Weapon || []).forEach(w => w.removeFromParent());
    (loadedParts[slot] || []).forEach(dispose); delete loadedParts[slot];
    const paths = item?.parts[gender] || [];
    if (paths.length) {
      onStatus(`Loading ${item.name}…`);
      try {
        // Dual-wielded daggers: one copy per hand.
        const sources = slot === 'Weapon' && paths.length === 1 && /\/Weapon\/DA\//.test(paths[0]) ? [paths[0], paths[0]] : paths;
        const parts = await Promise.all(sources.map(loadPart));
        parts.forEach((p, i) => { if (i && sources[i] === sources[0]) p.userData.copy = i; p.userData.weapon = slot === 'Weapon'; });
        if (token !== loadToken || equipped[slot] !== item) { parts.forEach(dispose); return; }
        loadedParts[slot] = parts;
        if (slot !== 'Weapon') parts.forEach(p => character.add(p));
        onStatus('');
      } catch (e) { onError(`Could not load ${item.name}: ${e.message}`); onStatus(''); }
    }
    if (slot === 'Body' || slot === 'Weapon') attachWeapons();
    applyLookColors();
    updateVisibility(); onChange();
    await updatePose();
  }

  let framed = null;   // bounds of the last framing, reused when only the viewport changes
  function frame(measure = !framed) {
    if (measure) {
      // Skinned vertices are measured through bone world matrices, which three.js only
      // refreshes when rendering; update them so a just-applied pose is what gets measured.
      character.updateMatrixWorld(true);
      // Visible meshes only (hidden base parts stay loaded); the floor sits under the body,
      // not under a weapon planted in the ground.
      const all = new THREE.Box3(), body = new THREE.Box3();
      const visit = (node, weapon) => {
        if (!node.visible) return;
        weapon ||= node.userData.weapon;
        if (node.isMesh) { all.expandByObject(node, true); if (!weapon) body.expandByObject(node, true); }
        node.children.forEach(child => visit(child, weapon));
      };
      visit(character, false);
      if (all.isEmpty()) return;
      // Fit from the feet up; anything below them (a planted blade) may leave the frame.
      if (!body.isEmpty()) all.min.y = body.min.y;
      framed = {size: all.getSize(new THREE.Vector3()), center: all.getCenter(new THREE.Vector3()), floor: all.min.y};
    }
    if (!framed) return;
    const {size, center} = framed;
    floor.position.y = framed.floor; floor.scale.setScalar(Math.max(size.x, size.z, size.y * 0.4) * 1.2);
    controls.target.copy(center);
    // Fit both height and width for the current aspect, from a slight three-quarter view
    // so held weapons read clearly.
    const vertical = THREE.MathUtils.degToRad(camera.fov) / 2;
    const horizontal = Math.atan(Math.tan(vertical) * camera.aspect);
    const distance = 1.15 * Math.max(size.y / 2 / Math.tan(vertical), Math.max(size.x, size.z) / 2 / Math.tan(horizontal));
    camera.position.copy(center).add(new THREE.Vector3(0.9, 0.1, 2.1).normalize().multiplyScalar(distance));
    camera.near = size.y / 100; camera.far = size.y * 50; camera.updateProjectionMatrix();
  }

  const catchErrors = promise => promise.catch(e => { onError(e.message); onStatus(''); });
  return {
    get gender() { return gender; },
    get equipped() { return {...equipped}; },
    canWear: item => Boolean(item?.slot && item.parts[gender]?.length),
    // Replace the whole look (used for a stored character); reloads the base body.
    setLook(nextGender, items, appearance = null) {
      gender = nextGender;
      look = appearance;
      for (const slot of SLOTS) delete equipped[slot];
      for (const item of items) if (item?.slot) equipped[item.slot] = item;
      return catchErrors(loadBase());
    },
    setGender(nextGender) { if (nextGender === gender) return Promise.resolve(); gender = nextGender; look = null; return catchErrors(loadBase()); },
    equip(item) {
      if (!item?.slot || !item.parts[gender]?.length) return false;
      equipped[item.slot] = item; catchErrors(loadSlot(item.slot)); return true;
    },
    unequip(slot) { if (!equipped[slot]) return; delete equipped[slot]; catchErrors(loadSlot(slot)); },
    clear() { for (const slot of SLOTS) if (equipped[slot]) { delete equipped[slot]; catchErrors(loadSlot(slot)); } },
    setPoseMode(mode) { poseMode = mode === 'bind' ? 'bind' : 'still'; catchErrors(updatePose()); },
    setHairUnderHelmet(on) { hairUnderHelmet = on; updateVisibility(); },
    hairColor() { const hair = hairMaterials()[0]; return hair ? '#' + hair.color.getHexString() : null; },
    setHairColor(hex) { hairMaterials().forEach(m => m.color.set(hex)); },
  };
}
