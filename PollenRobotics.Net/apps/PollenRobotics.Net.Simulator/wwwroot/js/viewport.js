// The 3D viewport.
//
// Builds an articulated rig for whichever robot is loaded and drives it from joint angles pushed by
// the simulation. The rigs are built from primitives rather than loaded from glTF: Pollen does not
// publish meshes under a licence this project could vendor, and a shape built from primitives that
// moves correctly reads far better than an accurate mesh that does not move at all.
//
// Reference frames. The SDK works in the robotics convention - z up, x forward, y left - and
// three.js is y up with z toward the viewer. The rigs are authored directly in three.js space, so
// joint angles map straight onto the axis each joint is named for. The only place the two frames
// meet is where a world pose is applied (the duck's body), and that conversion is written out
// once, there. Converting per joint instead is how a rig ends up with one axis inverted and nobody
// able to say which.

import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

const PALETTE = {
    dark: {
        ground: 0x14161a,
        grid: 0x2e343e,
        gridAccent: 0x3c444f,
        shell: 0xe8eaee,
        shellDark: 0x2a2f38,
        joint: 0xf5a524,
        accent: 0x8b7fd4,
        beak: 0xf5a524,
        limit: 0xe5484d,
    },
    light: {
        ground: 0xf6f5f2,
        grid: 0xd8d5cd,
        gridAccent: 0xc2bfb5,
        shell: 0xffffff,
        shellDark: 0x4a5160,
        joint: 0xc97f09,
        accent: 0x5b4dbe,
        beak: 0xd98e10,
        limit: 0xce2c31,
    },
};

let renderer, scene, camera, controls, root;
let rig = null;
let theme = 'dark';
let disposed = false;

/** Materials are shared across a rig and disposed with it. */
function makeMaterials(colors) {
    return {
        shell: new THREE.MeshStandardMaterial({ color: colors.shell, roughness: 0.42, metalness: 0.05 }),
        dark: new THREE.MeshStandardMaterial({ color: colors.shellDark, roughness: 0.55, metalness: 0.15 }),
        joint: new THREE.MeshStandardMaterial({ color: colors.joint, roughness: 0.35, metalness: 0.3 }),
        accent: new THREE.MeshStandardMaterial({ color: colors.accent, roughness: 0.4, metalness: 0.2 }),
        beak: new THREE.MeshStandardMaterial({ color: colors.beak, roughness: 0.5 }),
    };
}

function box(w, h, d, material) {
    return new THREE.Mesh(new THREE.BoxGeometry(w, h, d), material);
}

function cyl(rTop, rBottom, h, material, segments = 20) {
    return new THREE.Mesh(new THREE.CylinderGeometry(rTop, rBottom, h, segments), material);
}

function sphere(r, material, segments = 24) {
    return new THREE.Mesh(new THREE.SphereGeometry(r, segments, Math.max(8, segments / 2)), material);
}

// ---------------------------------------------------------------- Reachy Mini

/**
 * Reachy Mini: a cylindrical body, a six-branch neck and a rounded head with two antennas.
 *
 * The neck struts are drawn from their real solved lengths rather than being decorative: each one
 * is stretched and aimed between its base and platform anchor every frame, so the parallel
 * mechanism visibly behaves like a parallel mechanism.
 */
function buildReachyMini(materials, colors) {
    const group = new THREE.Group();

    const base = cyl(0.062, 0.070, 0.030, materials.dark, 40);
    base.position.y = 0.015;
    group.add(base);

    // Body yaw turns everything above it.
    const body = new THREE.Group();
    body.position.y = 0.030;
    group.add(body);

    // The torso stops well below the neck platform on purpose. Drawn full height it swallowed the
    // six struts, leaving only their tips poking through the collar - the parallel mechanism was
    // being computed correctly and rendered invisibly. The real robot has an open neck too.
    const torso = cyl(0.050, 0.058, 0.028, materials.shell, 40);
    torso.position.y = 0.014;
    body.add(torso);

    const collar = new THREE.Mesh(new THREE.TorusGeometry(0.049, 0.004, 10, 40), materials.joint);
    collar.rotation.x = Math.PI / 2;
    collar.position.y = 0.029;
    body.add(collar);

    // The neck platform. Head pose is applied here; the struts follow it.
    const neck = new THREE.Group();
    neck.position.y = 0.072;
    body.add(neck);

    const struts = [];
    const baseAnchors = [];
    const platformAnchors = [];

    for (let pair = 0; pair < 3; pair++) {
        const pairAngle = (pair * 2 * Math.PI) / 3;

        for (let side = 0; side < 2; side++) {
            const sign = side === 0 ? -1 : 1;
            const baseAngle = pairAngle + sign * THREE.MathUtils.degToRad(26);
            const platformAngle = pairAngle + THREE.MathUtils.degToRad(12) + sign * THREE.MathUtils.degToRad(20);

            baseAnchors.push(new THREE.Vector3(
                0.045 * Math.cos(baseAngle), 0, 0.045 * Math.sin(baseAngle)));

            platformAnchors.push(new THREE.Vector3(
                0.022 * Math.cos(platformAngle), 0, 0.022 * Math.sin(platformAngle)));

            const strut = cyl(0.0035, 0.0035, 1, materials.joint, 8);
            body.add(strut);
            struts.push(strut);
        }
    }

    const head = new THREE.Group();
    neck.add(head);

    const skull = sphere(0.044, materials.shell, 32);
    skull.scale.set(1, 0.92, 1.05);
    skull.position.y = 0.030;
    head.add(skull);

    // The visor faces +Z, which is the robot's front. It is the only cue that says which way the
    // head is pointing, so it is a flat disc rather than a hemisphere: unambiguous from any angle.
    const visor = sphere(0.029, materials.dark, 28);
    visor.scale.set(1, 0.72, 0.30);
    visor.position.set(0, 0.030, 0.034);
    head.add(visor);

    const antennas = [];

    for (const side of [-1, 1]) {
        const antenna = new THREE.Group();
        antenna.position.set(side * -0.012, 0.066, side * 0.026);
        head.add(antenna);

        const stalk = cyl(0.002, 0.002, 0.042, materials.joint, 8);
        stalk.position.y = 0.021;
        antenna.add(stalk);

        const tip = sphere(0.006, materials.joint, 12);
        tip.position.y = 0.044;
        antenna.add(tip);

        antennas.push(antenna);
    }

    return {
        group,
        materials,
        colors,
        body,
        neck,
        head,
        antennas,
        struts,
        baseAnchors,
        platformAnchors,

        apply(joints) {
            // Joint 0 is body yaw; 1..6 are the neck branches; 7 and 8 the antennas.
            this.body.rotation.y = joints[0] ?? 0;

            // The neck platform pose is not sent as joints - it is what the branches encode - so it
            // is reconstructed from the branch angles. A branch swinging up lifts its corner.
            const lift = [];
            let mean = 0;

            for (let i = 0; i < 6; i++) {
                const angle = joints[i + 1] ?? 0;
                const raise = Math.sin(angle) * 0.030;
                lift.push(raise);
                mean += raise;
            }

            mean /= 6;

            // Fit a plane through the six corner heights: the mean is the platform height, and the
            // first moments about x and z are its tilt.
            let tiltX = 0;
            let tiltZ = 0;

            for (let i = 0; i < 6; i++) {
                const anchor = this.platformAnchors[i];
                const radius = Math.max(0.001, Math.hypot(anchor.x, anchor.z));
                tiltX += ((lift[i] - mean) * anchor.z) / radius;
                tiltZ -= ((lift[i] - mean) * anchor.x) / radius;
            }

            this.neck.position.y = 0.072 + mean;
            this.neck.rotation.x = THREE.MathUtils.clamp(tiltX * 26, -0.75, 0.75);
            this.neck.rotation.z = THREE.MathUtils.clamp(tiltZ * 26, -0.75, 0.75);

            // The platform anchors are placed analytically rather than through localToWorld. The
            // neck's world matrix is only refreshed during render, so converting here would use the
            // pose from the previous frame - which is why the struts first drew as flat stubs lying
            // in the collar instead of spanning up to the platform.
            const platformEuler = new THREE.Euler(this.neck.rotation.x, 0, this.neck.rotation.z, 'XYZ');

            for (let i = 0; i < 6; i++) {
                // Base anchors are already in body-local space, where the base plate is y = 0.
                const from = this.baseAnchors[i];
                const to = this.platformAnchors[i]
                    .clone()
                    .applyEuler(platformEuler)
                    .add(new THREE.Vector3(0, this.neck.position.y, 0));

                aimCylinder(this.struts[i], from, to);
            }

            this.antennas[0].rotation.x = joints[7] ?? 0;
            this.antennas[1].rotation.x = joints[8] ?? 0;
        },
    };
}

/** Stretches and aims a unit-height cylinder so it spans two points. */
function aimCylinder(mesh, from, to) {
    const direction = new THREE.Vector3().subVectors(to, from);
    const length = direction.length();

    if (length < 1e-6) {
        mesh.visible = false;
        return;
    }

    mesh.visible = true;
    mesh.position.copy(from).addScaledVector(direction, 0.5);
    mesh.scale.set(1, length, 1);

    // CylinderGeometry runs along +Y, so the rotation is the one taking +Y onto the direction.
    mesh.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), direction.normalize());
}

// ------------------------------------------------------------------ MicroDuck

/** MicroDuck: two five-joint legs, a two-joint neck, a head and a beak. */
function buildMicroDuck(materials, colors) {
    const group = new THREE.Group();

    const pelvis = new THREE.Group();
    pelvis.position.y = 0.135;
    group.add(pelvis);

    const belly = sphere(0.052, materials.shell, 28);
    belly.scale.set(0.85, 1.0, 1.15);
    pelvis.add(belly);

    const tail = new THREE.Mesh(new THREE.ConeGeometry(0.026, 0.05, 12), materials.shell);
    tail.position.set(0, 0.012, -0.058);
    tail.rotation.x = -Math.PI / 2.4;
    pelvis.add(tail);

    // Neck and head hang off the body, forward and up.
    const neckPitch = new THREE.Group();
    neckPitch.position.set(0, 0.042, 0.030);
    pelvis.add(neckPitch);

    const neckYaw = new THREE.Group();
    neckPitch.add(neckYaw);

    const neckTube = cyl(0.013, 0.016, 0.055, materials.shell, 16);
    neckTube.position.y = 0.027;
    neckYaw.add(neckTube);

    const headPitch = new THREE.Group();
    headPitch.position.y = 0.055;
    neckYaw.add(headPitch);

    const headRoll = new THREE.Group();
    headPitch.add(headRoll);

    const skull = sphere(0.030, materials.shell, 24);
    skull.scale.set(0.95, 0.9, 1.05);
    headRoll.add(skull);

    for (const side of [-1, 1]) {
        const eye = sphere(0.006, materials.dark, 12);
        eye.position.set(side * 0.018, 0.010, 0.020);
        headRoll.add(eye);
    }

    const upperBeak = new THREE.Mesh(new THREE.ConeGeometry(0.016, 0.040, 4), materials.beak);
    upperBeak.rotation.set(Math.PI / 2, 0, Math.PI / 4);
    upperBeak.position.set(0, -0.002, 0.042);
    headRoll.add(upperBeak);

    const lowerBeakPivot = new THREE.Group();
    lowerBeakPivot.position.set(0, -0.004, 0.022);
    headRoll.add(lowerBeakPivot);

    const lowerBeak = new THREE.Mesh(new THREE.ConeGeometry(0.013, 0.034, 4), materials.beak);
    lowerBeak.rotation.set(Math.PI / 2, 0, Math.PI / 4);
    lowerBeak.position.set(0, -0.004, 0.017);
    lowerBeakPivot.add(lowerBeak);

    const legs = [];

    for (const side of [1, -1]) {
        const hipYaw = new THREE.Group();
        hipYaw.position.set(side * 0.028, -0.030, 0);
        pelvis.add(hipYaw);

        const hipRoll = new THREE.Group();
        hipYaw.add(hipRoll);

        const hipPitch = new THREE.Group();
        hipRoll.add(hipPitch);

        const thigh = cyl(0.011, 0.010, 0.058, materials.dark, 12);
        thigh.position.y = -0.029;
        hipPitch.add(thigh);

        const knee = new THREE.Group();
        knee.position.y = -0.058;
        hipPitch.add(knee);

        const shin = cyl(0.009, 0.008, 0.050, materials.dark, 12);
        shin.position.y = -0.025;
        knee.add(shin);

        const ankle = new THREE.Group();
        ankle.position.y = -0.050;
        knee.add(ankle);

        const foot = box(0.030, 0.010, 0.052, materials.joint);
        foot.position.set(0, -0.005, 0.010);
        ankle.add(foot);

        legs.push({ hipYaw, hipRoll, hipPitch, knee, ankle });
    }

    return {
        group,
        materials,
        colors,
        pelvis,
        neckPitch,
        neckYaw,
        headPitch,
        headRoll,
        lowerBeakPivot,
        legs,

        apply(joints, body) {
            // Catalogue order: left leg 0..4, right leg 5..9, neck 10..11, head 12..13, beak 14.
            for (let i = 0; i < 2; i++) {
                const leg = this.legs[i];
                const base = i * 5;

                leg.hipYaw.rotation.y = joints[base] ?? 0;
                leg.hipRoll.rotation.z = joints[base + 1] ?? 0;
                leg.hipPitch.rotation.x = joints[base + 2] ?? 0;
                leg.knee.rotation.x = -(joints[base + 3] ?? 0);
                leg.ankle.rotation.x = joints[base + 4] ?? 0;
            }

            this.neckPitch.rotation.x = joints[10] ?? 0;
            this.neckYaw.rotation.y = joints[11] ?? 0;
            this.headPitch.rotation.x = joints[12] ?? 0;
            this.headRoll.rotation.z = joints[13] ?? 0;
            this.lowerBeakPivot.rotation.x = joints[14] ?? 0;

            // The duck walks, so its body pose moves in the world rather than staying at the origin.
            if (body) {
                this.group.position.set(body.x, body.z, -body.y);
                this.group.rotation.y = body.yaw;
                this.pelvis.rotation.x = body.pitch;
            }
        },
    };
}

// ------------------------------------------------------------------- Reachy 2

/** Reachy 2: a torso on a mobile base, two seven-axis arms, an Orbita neck and two antennas. */
function buildReachy2(materials, colors) {
    const group = new THREE.Group();

    const base = cyl(0.20, 0.22, 0.10, materials.dark, 32);
    base.position.y = 0.05;
    group.add(base);

    const column = cyl(0.055, 0.065, 0.42, materials.shell, 24);
    column.position.y = 0.31;
    group.add(column);

    const torso = box(0.16, 0.22, 0.11, materials.shell);
    torso.position.y = 0.62;
    group.add(torso);

    const neckRoll = new THREE.Group();
    neckRoll.position.y = 0.75;
    group.add(neckRoll);

    const neckPitch = new THREE.Group();
    neckRoll.add(neckPitch);

    const neckYaw = new THREE.Group();
    neckPitch.add(neckYaw);

    const orbita = sphere(0.030, materials.joint, 20);
    neckYaw.add(orbita);

    const head = box(0.10, 0.11, 0.085, materials.shell);
    head.position.y = 0.075;
    neckYaw.add(head);

    const visor = box(0.085, 0.042, 0.006, materials.dark);
    visor.position.set(0, 0.082, 0.045);
    neckYaw.add(visor);

    const antennas = [];

    for (const side of [1, -1]) {
        const antenna = new THREE.Group();
        antenna.position.set(side * 0.042, 0.125, 0);
        neckYaw.add(antenna);

        const stalk = cyl(0.0035, 0.0035, 0.07, materials.accent, 8);
        stalk.position.y = 0.035;
        antenna.add(stalk);

        const tip = sphere(0.008, materials.accent, 12);
        tip.position.y = 0.072;
        antenna.add(tip);

        antennas.push(antenna);
    }

    const arms = [];

    for (const side of [1, -1]) {
        const shoulderPitch = new THREE.Group();
        shoulderPitch.position.set(side * 0.105, 0.70, 0);
        group.add(shoulderPitch);

        const shoulderRoll = new THREE.Group();
        shoulderPitch.add(shoulderRoll);

        const shoulderBall = sphere(0.032, materials.joint, 20);
        shoulderRoll.add(shoulderBall);

        const elbowYaw = new THREE.Group();
        shoulderRoll.add(elbowYaw);

        const upperArm = cyl(0.024, 0.022, 0.26, materials.shell, 16);
        upperArm.position.y = -0.13;
        elbowYaw.add(upperArm);

        const elbowPitch = new THREE.Group();
        elbowPitch.position.y = -0.26;
        elbowYaw.add(elbowPitch);

        const elbowBall = sphere(0.026, materials.joint, 16);
        elbowPitch.add(elbowBall);

        const wristRoll = new THREE.Group();
        elbowPitch.add(wristRoll);

        const forearm = cyl(0.021, 0.019, 0.24, materials.shell, 16);
        forearm.position.y = -0.12;
        wristRoll.add(forearm);

        const wristPitch = new THREE.Group();
        wristPitch.position.y = -0.24;
        wristRoll.add(wristPitch);

        const wristYaw = new THREE.Group();
        wristPitch.add(wristYaw);

        const wristBall = sphere(0.022, materials.joint, 16);
        wristYaw.add(wristBall);

        const palm = box(0.05, 0.05, 0.028, materials.dark);
        palm.position.y = -0.038;
        wristYaw.add(palm);

        // Two parallel jaws, opened by the gripper joint.
        const jaws = [];

        for (const jawSide of [1, -1]) {
            const jaw = new THREE.Group();
            jaw.position.set(jawSide * 0.014, -0.062, 0);
            wristYaw.add(jaw);

            const finger = box(0.011, 0.048, 0.022, materials.accent);
            finger.position.y = -0.022;
            jaw.add(finger);

            jaws.push({ jaw, sign: jawSide });
        }

        arms.push({ shoulderPitch, shoulderRoll, elbowYaw, elbowPitch, wristRoll, wristPitch, wristYaw, jaws });
    }

    return {
        group,
        materials,
        colors,
        neckRoll,
        neckPitch,
        neckYaw,
        antennas,
        arms,

        apply(joints) {
            // Catalogue order: r_arm 0..6, l_arm 7..13, neck 14..16, antennas 17..18, grippers 19..20.
            for (let i = 0; i < 2; i++) {
                const arm = this.arms[i];
                const base = i * 7;

                arm.shoulderPitch.rotation.x = joints[base] ?? 0;
                arm.shoulderRoll.rotation.z = joints[base + 1] ?? 0;
                arm.elbowYaw.rotation.y = joints[base + 2] ?? 0;
                arm.elbowPitch.rotation.x = joints[base + 3] ?? 0;
                arm.wristRoll.rotation.y = joints[base + 4] ?? 0;
                arm.wristPitch.rotation.x = joints[base + 5] ?? 0;
                arm.wristYaw.rotation.z = joints[base + 6] ?? 0;

                const opening = THREE.MathUtils.clamp(joints[19 + i] ?? 0, 0, 2.3);

                for (const { jaw, sign } of arm.jaws) {
                    jaw.position.x = sign * (0.008 + opening * 0.012);
                }
            }

            this.neckRoll.rotation.z = joints[14] ?? 0;
            this.neckPitch.rotation.x = joints[15] ?? 0;
            this.neckYaw.rotation.y = joints[16] ?? 0;

            this.antennas[0].rotation.x = joints[17] ?? 0;
            this.antennas[1].rotation.x = joints[18] ?? 0;
        },
    };
}

const BUILDERS = {
    ReachyMini: buildReachyMini,
    MicroDuck: buildMicroDuck,
    Reachy2: buildReachy2,
};

// ---------------------------------------------------------------- scene setup

function disposeRig() {
    if (!rig) {
        return;
    }

    root.remove(rig.group);

    rig.group.traverse((node) => {
        if (node.isMesh) {
            node.geometry.dispose();
        }
    });

    for (const material of Object.values(rig.materials)) {
        material.dispose();
    }

    rig = null;
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

    scene.add(new THREE.AmbientLight(0xffffff, 0.55));

    const key = new THREE.DirectionalLight(0xffffff, 1.5);
    key.position.set(0.8, 1.4, 0.9);
    scene.add(key);

    const fill = new THREE.DirectionalLight(0x9fb4d8, 0.6);
    fill.position.set(-0.9, 0.4, -0.7);
    scene.add(fill);

    const rim = new THREE.DirectionalLight(0xf5a524, 0.35);
    rim.position.set(0, 0.3, -1.2);
    scene.add(rim);

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
    if (!renderer) {
        return;
    }

    const canvas = renderer.domElement;
    const width = canvas.clientWidth || 640;
    const height = canvas.clientHeight || 480;

    // false: let CSS own the element size, and only resize the drawing buffer. Passing true here
    // makes the canvas fight its own layout and grow a few pixels every frame.
    renderer.setSize(width, height, false);
    camera.aspect = width / height;
    camera.updateProjectionMatrix();
}

export function loadRobot(kind) {
    disposeRig();

    const builder = BUILDERS[kind];

    if (!builder) {
        report('error', `No rig is defined for ${kind}.`);
        return false;
    }

    const colors = PALETTE[theme];
    rig = builder(makeMaterials(colors), colors);
    root.add(rig.group);

    // Frame each robot for its own size. Reachy Mini sits on a desk, the duck stands about 25 cm
    // and Reachy 2 is person-sized, so one camera position cannot serve all three - the duck's head
    // was being cut off by the framing that suited the Mini.
    const framing = {
        ReachyMini: { camera: [0.40, 0.30, 0.46], target: [0, 0.13, 0], maxDistance: 4 },
        MicroDuck: { camera: [0.62, 0.46, 0.72], target: [0, 0.15, 0], maxDistance: 6 },
        Reachy2: { camera: [1.5, 1.2, 1.8], target: [0, 0.62, 0], maxDistance: 8 },
    }[kind] ?? { camera: [0.5, 0.4, 0.6], target: [0, 0.2, 0], maxDistance: 5 };

    camera.position.set(...framing.camera);
    controls.target.set(...framing.target);
    controls.maxDistance = framing.maxDistance;

    controls.update();
    return true;
}

export function setTheme(next) {
    theme = next === 'light' ? 'light' : 'dark';
    document.documentElement.dataset.theme = theme;

    if (!scene) {
        return;
    }

    applyTheme();

    // Rebuild the rig so its materials pick up the new palette. currentKind is the only reliable
    // record of what is loaded - an earlier version looked for a `builder` property the rigs never
    // had, so the rig silently kept its old colours.
    if (rig && currentKind) {
        loadRobot(currentKind);
    }
}

let currentKind = null;

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
            return;
        }

        currentKind = kind;
        report('info', `Rig loaded: ${kind}.`);
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
        renderer.dispose();
    }
}
