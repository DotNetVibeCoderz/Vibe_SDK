// The 3D viewport.
//
// Loads an articulated glTF model for whichever robot is selected and drives it from joint angles
// pushed by the simulation. The models are built by tools/blender/build_models.py from the
// photographs in images/, and the .glb files are generated output - edit the Python, not the
// binaries.
//
// Reference frames. The SDK works in the robotics convention - z up, x forward, y left - and
// three.js is y up with z toward the viewer. The models are authored in Blender with z up and the
// robot facing -y, and the exporter's y-up conversion turns that into exactly the three.js frame
// the rest of this file assumes: x right, y up, z forward. The only place the two frames meet is
// where a world pose is applied (the duck's body), and that conversion is written out once, there.
//
// Articulation is by node name. Every joint in a model is an empty named for its SDK joint -
// `left.knee`, `r_arm.elbow.pitch` - so a frame is applied by looking the name up and writing a
// rotation. Driving meshes by index instead breaks silently the moment a model gains a part.

import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';

const PALETTE = {
    dark: {
        ground: 0x14161a,
        grid: 0x2e343e,
        gridAccent: 0x3c444f,
        ambient: 0.55,
        key: 1.5,
    },
    light: {
        ground: 0xf6f5f2,
        grid: 0xd8d5cd,
        gridAccent: 0xc2bfb5,
        // A white robot on a near-white ground needs less fill and a harder key, or it flattens
        // into the background and every edge disappears.
        ambient: 0.40,
        key: 1.9,
    },
};

const MODELS = {
    ReachyMini: 'reachy-mini',
    MicroDuck: 'microduck',
    Reachy2: 'reachy2',
};

let renderer, scene, camera, controls, root, lights;
let rig = null;
let theme = 'dark';
let disposed = false;
let currentKind = null;

// Models are loaded once and kept. Reloading on every robot switch re-parses 180 KB of glTF and
// drops the GPU buffers for something that is about to be needed again.
const cache = new Map();
const pending = new Set();

// ---------------------------------------------------------------- Reachy Mini geometry
//
// These must agree with tools/blender/reachy_mini.py. They are the strut anchor ring that the
// viewport re-aims every frame, and there is no way to read them back out of a .glb.

const MINI_STRUT_BASE_RADIUS = 0.0345;
const MINI_STRUT_PLATFORM_RADIUS = 0.0255;
const MINI_STRUT_BASE_Y = 0.082;
const MINI_NECK_Y = 0.140;
const MINI_PLATFORM_Y = 0.138;

const MINI_ANCHORS = [];

for (let pair = 0; pair < 3; pair++) {
    for (const sign of [-1, 1]) {
        const base = (pair * 2 * Math.PI) / 3 + sign * THREE.MathUtils.degToRad(26);
        const platform = (pair * 2 * Math.PI) / 3 + THREE.MathUtils.degToRad(12) + sign * THREE.MathUtils.degToRad(20);

        MINI_ANCHORS.push({
            // Blender (x, y, z) exports as three.js (x, z, -y).
            base: new THREE.Vector3(
                MINI_STRUT_BASE_RADIUS * Math.cos(base), MINI_STRUT_BASE_Y, -MINI_STRUT_BASE_RADIUS * Math.sin(base)),
            platform: new THREE.Vector3(
                MINI_STRUT_PLATFORM_RADIUS * Math.cos(platform), 0, -MINI_STRUT_PLATFORM_RADIUS * Math.sin(platform)),
        });
    }
}

// ---------------------------------------------------------------- helpers

/** Stretches and aims a unit-height cylinder so it spans two points. */
const AIM_UP = new THREE.Vector3(0, 1, 0);
const aimDirection = new THREE.Vector3();
const aimMidpoint = new THREE.Vector3();

function aimCylinder(mesh, from, to) {
    aimDirection.subVectors(to, from);
    const length = aimDirection.length();

    if (length < 1e-6) {
        mesh.visible = false;
        return;
    }

    mesh.visible = true;
    aimMidpoint.addVectors(from, to).multiplyScalar(0.5);
    mesh.position.copy(aimMidpoint);
    mesh.quaternion.setFromUnitVectors(AIM_UP, aimDirection.divideScalar(length));
    mesh.scale.set(1, length, 1);
}

/**
 * The name a joint has once it is inside a .glb.
 *
 * three.js strips `. : / [ ]` out of every node name while loading, so `left.hip_yaw` would arrive
 * as `lefthip_yaw` and a lookup by the catalogue's own name would quietly find nothing - which is
 * exactly how the first load of these models failed. The Blender build writes underscores for the
 * same reason (`node_name` in tools/blender/common.py); this is the matching half.
 */
function nodeName(joint) {
    return joint.replace(/\./g, '_');
}

/** Indexes every named node in a loaded model, keyed by the joint name the SDK uses. */
function indexNodes(model) {
    const nodes = new Map();
    model.traverse((node) => nodes.set(node.name, node));

    return {
        get: (joint) => nodes.get(nodeName(joint)),
        has: (joint) => nodes.has(nodeName(joint)),
    };
}

/** Looks joints up, reporting if a model and this file have drifted apart. */
function requireNodes(nodes, names, kind) {
    const missing = names.filter((name) => !nodes.has(name));

    if (missing.length > 0) {
        report('error', `The ${kind} model is missing: ${missing.join(', ')}. Rebuild it with tools/blender/build_models.py.`);
        return false;
    }

    return true;
}

// ---------------------------------------------------------------- drivers

function driveReachyMini(model, nodes) {
    const body = nodes.get('body');
    const neck = nodes.get('neck');
    const antennas = [nodes.get('antenna.right'), nodes.get('antenna.left')];
    const struts = [0, 1, 2, 3, 4, 5].map((i) => nodes.get(`strut.${i}`));

    const platformEuler = new THREE.Euler(0, 0, 0, 'XYZ');
    const anchorTo = new THREE.Vector3();

    return {
        group: model,

        apply(joints) {
            // Joint 0 is body yaw; 1..6 are the neck branches; 7 and 8 the antennas.
            body.rotation.y = joints[0] ?? 0;

            // The neck platform pose is not sent as joints - it is what the branches encode - so it
            // is reconstructed from the branch angles. A branch swinging up lifts its corner.
            let mean = 0;
            const lift = [];

            for (let i = 0; i < 6; i++) {
                const raise = Math.sin(joints[i + 1] ?? 0) * 0.030;
                lift.push(raise);
                mean += raise;
            }

            mean /= 6;

            // Fit a plane through the six corner heights: the mean is the platform height, and the
            // first moments about x and z are its tilt.
            let tiltX = 0;
            let tiltZ = 0;

            for (let i = 0; i < 6; i++) {
                const anchor = MINI_ANCHORS[i].platform;
                const radius = Math.max(0.001, Math.hypot(anchor.x, anchor.z));
                tiltX += ((lift[i] - mean) * -anchor.z) / radius;
                tiltZ -= ((lift[i] - mean) * anchor.x) / radius;
            }

            neck.position.y = MINI_NECK_Y + mean;
            neck.rotation.x = THREE.MathUtils.clamp(tiltX * 26, -0.75, 0.75);
            neck.rotation.z = THREE.MathUtils.clamp(tiltZ * 26, -0.75, 0.75);

            // The platform anchors are placed analytically rather than through localToWorld. A
            // node's world matrix is only refreshed during render, so converting here would use the
            // pose from the previous frame - which is how the struts first came to draw as flat
            // stubs lying in the collar instead of spanning up to the platform.
            platformEuler.set(neck.rotation.x, 0, neck.rotation.z, 'XYZ');

            for (let i = 0; i < 6; i++) {
                anchorTo.copy(MINI_ANCHORS[i].platform)
                    .applyEuler(platformEuler)
                    .setY(MINI_PLATFORM_Y + mean);

                aimCylinder(struts[i], MINI_ANCHORS[i].base, anchorTo);
            }

            antennas[0].rotation.x = joints[7] ?? 0;
            antennas[1].rotation.x = joints[8] ?? 0;
        },
    };
}

function driveMicroDuck(model, nodes) {
    const body = nodes.get('body');

    // The catalogue lists the left leg first, so index 0 is left.
    const legs = ['left', 'right'].map((side) => ({
        hipYaw: nodes.get(`${side}.hip_yaw`),
        hipRoll: nodes.get(`${side}.hip_roll`),
        hipPitch: nodes.get(`${side}.hip_pitch`),
        knee: nodes.get(`${side}.knee`),
        ankle: nodes.get(`${side}.ankle_pitch`),
    }));

    const neckPitch = nodes.get('neck.pitch');
    const neckYaw = nodes.get('neck.yaw');
    const headPitch = nodes.get('head.pitch');
    const headRoll = nodes.get('head.roll');
    const beak = nodes.get('beak');

    return {
        group: model,

        apply(joints, worldPose) {
            // Catalogue order: left leg 0..4, right leg 5..9, neck 10..11, head 12..13, beak 14.
            for (let i = 0; i < 2; i++) {
                const leg = legs[i];
                const base = i * 5;

                leg.hipYaw.rotation.y = joints[base] ?? 0;
                leg.hipRoll.rotation.z = joints[base + 1] ?? 0;
                leg.hipPitch.rotation.x = joints[base + 2] ?? 0;
                leg.knee.rotation.x = -(joints[base + 3] ?? 0);
                leg.ankle.rotation.x = joints[base + 4] ?? 0;
            }

            neckPitch.rotation.x = joints[10] ?? 0;
            neckYaw.rotation.y = joints[11] ?? 0;
            headPitch.rotation.x = joints[12] ?? 0;
            headRoll.rotation.z = joints[13] ?? 0;
            beak.rotation.x = joints[14] ?? 0;

            // The duck walks, so its body pose moves in the world rather than staying at the
            // origin. This is the one place the robotics frame meets the three.js one.
            if (worldPose) {
                model.position.set(worldPose.x, worldPose.z, -worldPose.y);
                model.rotation.y = worldPose.yaw;
                body.rotation.x = worldPose.pitch;
            }
        },
    };
}

function driveReachy2(model, nodes) {
    const arms = ['r_arm', 'l_arm'].map((prefix) => ({
        shoulderPitch: nodes.get(`${prefix}.shoulder.pitch`),
        shoulderRoll: nodes.get(`${prefix}.shoulder.roll`),
        elbowYaw: nodes.get(`${prefix}.elbow.yaw`),
        elbowPitch: nodes.get(`${prefix}.elbow.pitch`),
        wristRoll: nodes.get(`${prefix}.wrist.roll`),
        wristPitch: nodes.get(`${prefix}.wrist.pitch`),
        wristYaw: nodes.get(`${prefix}.wrist.yaw`),
        fingers: [
            { node: nodes.get(`${prefix}.finger.a`), sign: -1 },
            { node: nodes.get(`${prefix}.finger.b`), sign: 1 },
        ],
    }));

    const neckRoll = nodes.get('head.neck.roll');
    const neckPitch = nodes.get('head.neck.pitch');
    const neckYaw = nodes.get('head.neck.yaw');
    const antennas = [nodes.get('head.r_antenna'), nodes.get('head.l_antenna')];

    return {
        group: model,

        apply(joints) {
            // Catalogue order: r_arm 0..6, l_arm 7..13, neck 14..16, antennas 17..18, grippers 19..20.
            for (let i = 0; i < 2; i++) {
                const arm = arms[i];
                const base = i * 7;

                arm.shoulderPitch.rotation.x = joints[base] ?? 0;
                arm.shoulderRoll.rotation.z = joints[base + 1] ?? 0;
                arm.elbowYaw.rotation.y = joints[base + 2] ?? 0;
                arm.elbowPitch.rotation.x = joints[base + 3] ?? 0;
                arm.wristRoll.rotation.y = joints[base + 4] ?? 0;
                arm.wristPitch.rotation.x = joints[base + 5] ?? 0;
                arm.wristYaw.rotation.z = joints[base + 6] ?? 0;

                // The gripper is reported as an opening, not an angle: the fingers slide apart.
                const opening = THREE.MathUtils.clamp(joints[19 + i] ?? 0, 0, 2.3);

                for (const { node, sign } of arm.fingers) {
                    node.position.x = sign * (0.012 + opening * 0.011);
                }
            }

            neckRoll.rotation.z = joints[14] ?? 0;
            neckPitch.rotation.x = joints[15] ?? 0;
            neckYaw.rotation.y = joints[16] ?? 0;

            antennas[0].rotation.x = joints[17] ?? 0;
            antennas[1].rotation.x = joints[18] ?? 0;
        },
    };
}

const DRIVERS = {
    ReachyMini: {
        drive: driveReachyMini,
        joints: ['body', 'neck', 'antenna.right', 'antenna.left', 'strut.0', 'strut.5'],
        framing: { camera: [0.38, 0.32, 0.46], target: [0, 0.16, 0], maxDistance: 4 },
    },
    MicroDuck: {
        drive: driveMicroDuck,
        joints: ['body', 'left.knee', 'right.knee', 'neck.pitch', 'neck.yaw', 'head.pitch', 'head.roll', 'beak'],
        framing: { camera: [0.52, 0.40, 0.62], target: [0, 0.16, 0], maxDistance: 6 },
    },
    Reachy2: {
        drive: driveReachy2,
        joints: ['r_arm.shoulder.pitch', 'l_arm.shoulder.pitch', 'head.neck.yaw', 'head.r_antenna', 'head.l_antenna'],
        framing: { camera: [1.55, 1.30, 1.90], target: [0, 0.80, 0], maxDistance: 8 },
    },
};

// ---------------------------------------------------------------- scene setup

function disposeRig() {
    if (rig) {
        root.remove(rig.group);
        rig = null;
    }
}

function applyTheme() {
    const colors = PALETTE[theme];
    scene.background = new THREE.Color(colors.ground);

    const grid = scene.getObjectByName('grid');

    if (grid) {
        scene.remove(grid);
        grid.geometry.dispose();
        grid.material.dispose();
    }

    const replacement = new THREE.GridHelper(2.4, 24, colors.gridAccent, colors.grid);
    replacement.name = 'grid';
    replacement.material.transparent = true;
    replacement.material.opacity = 0.55;
    scene.add(replacement);

    // The robots keep the colours they are painted. These are real products with a real palette -
    // a white shell, a navy striped shirt, an orange beak - and re-tinting them per theme was only
    // ever a way of making primitives readable. Light adapts instead.
    if (lights) {
        lights.ambient.intensity = colors.ambient;
        lights.key.intensity = colors.key;
    }
}

// Set by init(): a .NET object reference the viewport reports failures through.
//
// A desktop app has no console anyone will ever open, so a JavaScript error here is otherwise
// completely silent - the canvas simply stays empty. Everything that can fail routes through
// report() and lands in the same log panel as the rest of the simulator.
let reporter = null;

function report(level, message) {
    if (reporter) {
        reporter.invokeMethodAsync('ReportFromBrowser', level, String(message)).catch(() => {
            // The circuit is gone. Nothing left to report to.
        });
    }

    if (level === 'error') {
        console.error(`[viewport] ${message}`);
    }
}

export function init(canvasId, dotNetReporter) {
    reporter = dotNetReporter ?? null;

    window.addEventListener('error', (event) => report('error', event.message));
    window.addEventListener('unhandledrejection', (event) => report('error', event.reason?.message ?? event.reason));

    const canvas = document.getElementById(canvasId);

    if (!canvas) {
        report('error', `No canvas with id ${canvasId}.`);
        return false;
    }

    try {
        renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
    } catch (error) {
        report('error', `WebGL is unavailable: ${error.message}`);
        return false;
    }

    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    renderer.shadowMap.enabled = false;

    scene = new THREE.Scene();

    camera = new THREE.PerspectiveCamera(38, 1, 0.05, 40);
    camera.position.set(0.62, 0.48, 0.72);

    controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.dampingFactor = 0.08;
    controls.target.set(0, 0.16, 0);
    controls.minDistance = 0.25;
    controls.maxDistance = 4;
    // Stop the camera going under the floor, where the robot is a silhouette against nothing.
    controls.maxPolarAngle = Math.PI * 0.49;

    lights = {
        ambient: new THREE.AmbientLight(0xffffff, 0.55),
        key: new THREE.DirectionalLight(0xffffff, 1.5),
        fill: new THREE.DirectionalLight(0x9fb4d8, 0.6),
        rim: new THREE.DirectionalLight(0xf5a524, 0.35),
    };

    lights.key.position.set(0.8, 1.4, 0.9);
    lights.fill.position.set(-0.9, 0.4, -0.7);
    lights.rim.position.set(0, 0.3, -1.2);

    for (const light of Object.values(lights)) {
        scene.add(light);
    }

    // The bridge between the robotics frame and the three.js one. Everything hangs off this.
    root = new THREE.Group();
    scene.add(root);

    applyTheme();
    resize();

    renderer.setAnimationLoop(() => {
        if (disposed) {
            return;
        }

        try {
            controls.update();
            renderer.render(scene, camera);
        } catch (error) {
            // A throw inside the render loop repeats every frame. Stop the loop and say so once.
            renderer.setAnimationLoop(null);
            report('error', `Render loop stopped: ${error.message}`);
        }
    });

    window.addEventListener('resize', resize);
    report('info', 'Viewport ready.');
    return true;
}

export function resize() {
    if (!renderer || !camera) {
        return;
    }

    const canvas = renderer.domElement;
    const width = canvas.clientWidth || 1;
    const height = canvas.clientHeight || 1;

    renderer.setSize(width, height, false);
    camera.aspect = width / height;
    camera.updateProjectionMatrix();
}

/**
 * Makes a robot current, loading its model the first time it is asked for.
 *
 * Deliberately not async from the caller's point of view. Loading is started and `false` is
 * returned; the next frame asks again and gets the loaded model. Turning this into a promise would
 * push async all the way up into the 30 Hz frame loop in C# for an event that happens three times
 * in a session.
 */
export function loadRobot(kind) {
    const file = MODELS[kind];
    const driver = DRIVERS[kind];

    if (!file || !driver) {
        report('error', `No model is defined for ${kind}.`);
        return false;
    }

    const cached = cache.get(kind);

    if (cached) {
        disposeRig();

        const nodes = indexNodes(cached);

        if (!requireNodes(nodes, driver.joints, kind)) {
            return false;
        }

        rig = driver.drive(cached, nodes);
        root.add(cached);

        // Frame each robot for its own size. Reachy Mini sits on a desk, the duck stands about
        // 25 cm and Reachy 2 is person-sized, so one camera position cannot serve all three - and
        // these numbers belong to the current models: a model that grows needs its framing checked,
        // or the robot is quietly cropped at the top of the viewport.
        camera.position.set(...driver.framing.camera);
        controls.target.set(...driver.framing.target);
        controls.maxDistance = driver.framing.maxDistance;
        controls.update();

        return true;
    }

    if (!pending.has(kind)) {
        pending.add(kind);

        new GLTFLoader().load(
            `/models/${file}.glb`,
            (gltf) => {
                pending.delete(kind);
                cache.set(kind, gltf.scene);
                report('info', `Model loaded: ${file}.glb.`);
            },
            undefined,
            (error) => {
                pending.delete(kind);
                report('error', `Could not load /models/${file}.glb: ${error?.message ?? error}. `
                    + 'Run tools/blender/build_models.py to generate it.');
            });
    }

    return false;
}

export function setTheme(next) {
    theme = next === 'light' ? 'light' : 'dark';
    document.documentElement.dataset.theme = theme;

    if (scene) {
        applyTheme();
    }
}

/**
 * Applies one frame of joint angles.
 *
 * Called from C# at about 30 Hz. It does no allocation and no scene-graph surgery - just writes
 * rotations - so it is cheap enough to run every frame without competing with the render.
 */
export function applyFrame(kind, joints, body) {
    if (!renderer) {
        return;
    }

    if (!rig || currentKind !== kind) {
        if (!loadRobot(kind)) {
            // Still loading, or the model is missing. Either way there is nothing to pose yet.
            return;
        }

        currentKind = kind;
        report('info', `Rig ready: ${kind}.`);
    }

    try {
        rig.apply(joints, body);
        followRig();
    } catch (error) {
        report('error', `Applying a frame to the ${kind} rig failed: ${error.message}`);
        rig = null;
        currentKind = null;
    }
}

/**
 * Keeps a travelling robot in frame.
 *
 * The duck walks. Left alone the camera stays at the origin and the robot leaves the view within
 * about ten seconds, which reads as the viewport having stopped working. The target eases rather
 * than snapping so the user can still orbit while it follows.
 */
function followRig() {
    if (!rig || !controls) {
        return;
    }

    const position = rig.group.position;

    if (Math.abs(position.x) < 1e-4 && Math.abs(position.z) < 1e-4) {
        return;
    }

    const desired = new THREE.Vector3(position.x, controls.target.y, position.z);
    const drift = new THREE.Vector3().subVectors(desired, controls.target);

    // Move the camera with the target so the viewing angle is preserved.
    const step = drift.multiplyScalar(0.08);
    controls.target.add(step);
    camera.position.add(step);
}

export function dispose() {
    disposed = true;
    window.removeEventListener('resize', resize);

    if (renderer) {
        renderer.setAnimationLoop(null);
        disposeRig();

        for (const model of cache.values()) {
            model.traverse((node) => {
                if (node.isMesh) {
                    node.geometry.dispose();

                    for (const material of [node.material].flat()) {
                        material.dispose();
                    }
                }
            });
        }

        cache.clear();
        renderer.dispose();
    }
}
