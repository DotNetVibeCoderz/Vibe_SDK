// Robot viewport.
//
// The scene is built from the same rig description the simulation moves, so the geometry you see and
// the geometry that walks cannot drift apart. Each rig link becomes a nested Group, which means the
// scene graph performs forward kinematics for us — this module never multiplies a transform itself.
//
// Frames: Unitree (and ROS) use x forward, y left, z up. Three.js is y up. Rather than converting
// every coordinate, the whole robot hangs under one group rotated -90 degrees about x, which maps
// (x, y, z) to (x, z, -y). Rig numbers are then used verbatim, in metres.

import * as THREE from 'three';
import { OrbitControls } from '../lib/three/OrbitControls.js';

const THEMES = {
    dark: {
        background: 0x0f1319,
        fog: 0x0f1319,
        grid: 0x2a3543,
        gridAxis: 0x38cfe0,
        shell: 0xd8dee8,
        limb: 0x8d99ab,
        actuator: 0x38cfe0,
        contact: 0x39424f,
        contactLive: 0x38cfe0,
        sensor: 0xf2a33c,
        key: 0xffffff,
        keyIntensity: 2.1,
        fill: 0x5f7f9c,
        fillIntensity: 0.75,
        ambient: 0.45,
        ground: 0x141a22,
    },
    light: {
        background: 0xf4f3ef,
        fog: 0xf4f3ef,
        grid: 0xcfccc3,
        gridAxis: 0x0b7c8a,
        shell: 0xfbfbf9,
        limb: 0x9aa1ab,
        actuator: 0x0b7c8a,
        contact: 0x6d7480,
        contactLive: 0x0b7c8a,
        sensor: 0xa2650a,
        key: 0xffffff,
        keyIntensity: 2.4,
        fill: 0xc8d4de,
        fillIntensity: 0.9,
        ambient: 0.75,
        ground: 0xe6e4dd,
    },
};

const SURFACE = ['shell', 'limb', 'actuator', 'contact', 'sensor'];

let renderer = null;
let scene = null;
let camera = null;
let controls = null;
let resizeObserver = null;

let container = null;
let robotRoot = null;      // frame conversion node
let bodyNode = null;       // body pose (position + orientation)
let jointNodes = [];       // index -> { node, axis, sign }
let contactNodes = [];     // index -> mesh, in rig contact order
let materials = {};
let ground = null;
let grid = null;
let themeName = 'dark';

function disposeTree(object) {
    object.traverse((child) => {
        if (child.geometry) child.geometry.dispose();
    });
}

function makeMaterials(theme) {
    return {
        shell: new THREE.MeshStandardMaterial({ color: theme.shell, roughness: 0.42, metalness: 0.12 }),
        limb: new THREE.MeshStandardMaterial({ color: theme.limb, roughness: 0.55, metalness: 0.35 }),
        actuator: new THREE.MeshStandardMaterial({ color: theme.actuator, roughness: 0.35, metalness: 0.45 }),
        contact: new THREE.MeshStandardMaterial({ color: theme.contact, roughness: 0.9, metalness: 0.0 }),
        sensor: new THREE.MeshStandardMaterial({
            color: theme.sensor,
            roughness: 0.3,
            metalness: 0.2,
            emissive: theme.sensor,
            emissiveIntensity: 0.35,
        }),
    };
}

function buildShape(shape) {
    const [sx, sy] = [shape.size.x, shape.size.y];
    let geometry;

    switch (shape.kind) {
        case 0: // Box
            geometry = new THREE.BoxGeometry(shape.size.x, shape.size.y, shape.size.z);
            break;
        case 1: // Capsule: x = radius, y = length of the cylindrical section
            geometry = new THREE.CapsuleGeometry(sx, Math.max(sy, 0.001), 4, 12);
            break;
        case 2: // Cylinder
            geometry = new THREE.CylinderGeometry(sx, sx, Math.max(sy, 0.001), 18);
            break;
        default: // Sphere
            geometry = new THREE.SphereGeometry(sx, 18, 12);
            break;
    }

    const mesh = new THREE.Mesh(geometry, materials[SURFACE[shape.surface]] ?? materials.limb);
    mesh.castShadow = true;
    mesh.receiveShadow = true;
    mesh.position.set(shape.center.x, shape.center.y, shape.center.z);

    // Capsules and cylinders are built along Y. Re-aim them at the axis the rig asked for.
    if (shape.kind === 1 || shape.kind === 2) {
        if (shape.axisAlong === 0) mesh.rotation.z = Math.PI / 2;
        else if (shape.axisAlong === 2) mesh.rotation.x = Math.PI / 2;
    }

    return mesh;
}

function buildRobot(rig) {
    const nodes = new Map();
    const root = new THREE.Group();

    // Unitree x-forward/y-left/z-up into Three's y-up world.
    root.rotation.x = -Math.PI / 2;

    const body = new THREE.Group();
    root.add(body);

    jointNodes = new Array(rig.jointCount).fill(null);
    contactNodes = [];

    for (const link of rig.links) {
        const node = new THREE.Group();
        node.name = link.name;
        node.position.set(link.offset.x, link.offset.y, link.offset.z);

        for (const shape of link.shapes) {
            node.add(buildShape(shape));
        }

        const parent = link.parent ? nodes.get(link.parent) : body;
        (parent ?? body).add(node);
        nodes.set(link.name, node);

        if (link.jointIndex >= 0 && link.jointIndex < jointNodes.length) {
            jointNodes[link.jointIndex] = {
                node,
                axis: new THREE.Vector3(link.axis.x, link.axis.y, link.axis.z),
                sign: link.sign,
            };
        }
    }

    for (const name of rig.contactLinks) {
        const node = nodes.get(name);
        // The first mesh under a contact link is the pad or tyre. Lighting it when the foot is loaded
        // is what ties the 3D view back to the numbers in the status panel.
        const mesh = node ? node.children.find((child) => child.isMesh) : null;
        if (mesh) {
            mesh.material = mesh.material.clone();
            contactNodes.push(mesh);
        } else {
            contactNodes.push(null);
        }
    }

    return { root, body };
}

function buildStage(theme, reach) {
    const group = new THREE.Group();

    // A metre grid, so the robot's size is readable rather than merely plausible.
    grid = new THREE.GridHelper(20, 20, theme.gridAxis, theme.grid);
    grid.material.transparent = true;
    grid.material.opacity = 0.55;
    group.add(grid);

    const groundGeometry = new THREE.CircleGeometry(Math.max(6, reach * 5), 48);
    ground = new THREE.Mesh(
        groundGeometry,
        new THREE.MeshStandardMaterial({ color: theme.ground, roughness: 0.95, metalness: 0.0 }),
    );
    ground.rotation.x = -Math.PI / 2;
    ground.position.y = -0.002;
    ground.receiveShadow = true;
    group.add(ground);

    return group;
}

function applyTheme(theme) {
    scene.background = new THREE.Color(theme.background);
    scene.fog = new THREE.Fog(theme.fog, 6, 24);

    materials.shell.color.setHex(theme.shell);
    materials.limb.color.setHex(theme.limb);
    materials.actuator.color.setHex(theme.actuator);
    materials.contact.color.setHex(theme.contact);
    materials.sensor.color.setHex(theme.sensor);
    materials.sensor.emissive.setHex(theme.sensor);

    if (ground) ground.material.color.setHex(theme.ground);

    if (grid) {
        grid.material.color = new THREE.Color(theme.grid);
        grid.material.needsUpdate = true;
    }

    for (const light of scene.children) {
        if (light.isAmbientLight) light.intensity = theme.ambient;
        if (light.isDirectionalLight) {
            light.intensity = light.userData.role === 'key' ? theme.keyIntensity : theme.fillIntensity;
            light.color.setHex(light.userData.role === 'key' ? theme.key : theme.fill);
        }
    }
}

const api = {
    /**
     * Builds the scene for a rig. Safe to call again with a different rig — the previous one is
     * disposed first, which matters because switching models is a normal thing to do here.
     */
    init(selector, rigJson, theme) {
        themeName = theme === 'light' ? 'light' : 'dark';
        const palette = THEMES[themeName];
        // The rig arrives as JSON rather than as an interop object, so the property casing is fixed by
        // the C# serialiser rather than by whatever the interop layer happens to default to.
        const rig = typeof rigJson === 'string' ? JSON.parse(rigJson) : rigJson;

        container = document.querySelector(selector);
        if (!container) return false;

        api.dispose();

        scene = new THREE.Scene();
        materials = makeMaterials(palette);

        const reach = rig.standingHeight || 0.4;
        const frame = Math.max(1.0, reach * 3.2);

        camera = new THREE.PerspectiveCamera(38, 1, 0.05, 200);
        camera.position.set(frame * 0.95, reach * 1.55, frame * 1.05);

        renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false });
        renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
        renderer.shadowMap.enabled = true;
        renderer.shadowMap.type = THREE.PCFSoftShadowMap;
        container.appendChild(renderer.domElement);

        scene.add(new THREE.AmbientLight(0xffffff, palette.ambient));

        const key = new THREE.DirectionalLight(palette.key, palette.keyIntensity);
        key.position.set(2.6, 4.2, 2.0);
        key.castShadow = true;
        key.shadow.mapSize.set(2048, 2048);
        key.shadow.camera.near = 0.5;
        key.shadow.camera.far = 20;
        key.shadow.camera.left = -3;
        key.shadow.camera.right = 3;
        key.shadow.camera.top = 3;
        key.shadow.camera.bottom = -3;
        key.shadow.bias = -0.0008;
        key.userData.role = 'key';
        scene.add(key);

        const fill = new THREE.DirectionalLight(palette.fill, palette.fillIntensity);
        fill.position.set(-3.0, 1.4, -2.2);
        fill.userData.role = 'fill';
        scene.add(fill);

        scene.add(buildStage(palette, reach));

        const built = buildRobot(rig);
        robotRoot = built.root;
        bodyNode = built.body;
        bodyNode.position.z = rig.standingHeight;
        scene.add(robotRoot);

        controls = new OrbitControls(camera, renderer.domElement);
        controls.enableDamping = true;
        controls.dampingFactor = 0.07;
        controls.target.set(0, reach * 0.55, 0);
        controls.minDistance = reach * 1.2;
        controls.maxDistance = 14;
        // Stop the camera going under the floor, which reads as a bug even when it is not.
        controls.maxPolarAngle = Math.PI * 0.495;
        controls.update();

        applyTheme(palette);

        resizeObserver = new ResizeObserver(() => api.resize());
        resizeObserver.observe(container);
        api.resize();

        renderer.setAnimationLoop(() => {
            controls.update();
            renderer.render(scene, camera);
        });

        return true;
    },

    /**
     * Applies one simulation sample. Called about 50 times a second, so it allocates nothing beyond
     * the arrays it is handed and touches only what changed.
     */
    setPose(angles, height, roll, pitch, yaw, contacts) {
        if (!bodyNode) return;

        for (let i = 0; i < jointNodes.length && i < angles.length; i++) {
            const joint = jointNodes[i];
            if (!joint) continue;
            joint.node.setRotationFromAxisAngle(joint.axis, angles[i] * joint.sign);
        }

        bodyNode.position.z = height;
        // Robot-frame RPY, applied in the robot's own axes: the parent group handles y-up conversion.
        bodyNode.rotation.set(roll, pitch, yaw, 'ZYX');

        if (contacts) {
            for (let i = 0; i < contactNodes.length && i < contacts.length; i++) {
                const mesh = contactNodes[i];
                if (!mesh) continue;
                const loaded = Math.min(1, contacts[i] / 140);
                mesh.material.emissive.setHex(THEMES[themeName].contactLive);
                mesh.material.emissiveIntensity = loaded * 0.9;
            }
        }
    },

    setTheme(theme) {
        themeName = theme === 'light' ? 'light' : 'dark';
        if (scene) applyTheme(THEMES[themeName]);
    },

    resize() {
        if (!renderer || !container) return;
        const { clientWidth: w, clientHeight: h } = container;
        if (w === 0 || h === 0) return;
        camera.aspect = w / h;
        camera.updateProjectionMatrix();
        renderer.setSize(w, h, false);
    },

    dispose() {
        if (resizeObserver) {
            resizeObserver.disconnect();
            resizeObserver = null;
        }

        if (renderer) {
            renderer.setAnimationLoop(null);
            if (scene) disposeTree(scene);
            renderer.domElement.remove();
            renderer.dispose();
            renderer = null;
        }

        controls?.dispose();
        controls = null;
        scene = null;
        robotRoot = null;
        bodyNode = null;
        jointNodes = [];
        contactNodes = [];
    },
};

window.unitreeViewport = api;
window.dispatchEvent(new Event('unitree:viewport-ready'));
