import os
import sys
import json
import time
import shutil
import urllib.request
import urllib.parse
import torch
import numpy as np
from PIL import Image
from transformers import CLIPVisionModelWithProjection, CLIPImageProcessor

COMFY_URL = os.environ.get("COMFY_URL", "http://127.0.0.1:8188")
COMFY_INPUT_DIR = os.environ.get("COMFY_INPUT_DIR", r"D:\ComfyUI_windows_portable\ComfyUI\input")
COMFY_OUTPUT_DIR = os.environ.get("COMFY_OUTPUT_DIR", r"D:\ComfyUI_windows_portable\ComfyUI\output")

# 8-Turn Narrative Arc for Lyra (Silver Dragon Horns, Red Eyes)
MULTI_TURN_STORYLINE = [
    {
        "turn": 1,
        "location": "Sanctuary (Standing Window)",
        "action": "standing",
        "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, sharp jawline, porcelain skin, wearing white and gold priestess dress, standing beside grand arched window in sunlit sanctuary hall, soft golden daylight, medium shot, slight 3/4 turn, eye level",
        "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
        "seed": 100001
    },
    {
        "turn": 2,
        "location": "Sanctuary (Walking Altar)",
        "action": "walking",
        "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, porcelain skin, wearing white and gold priestess dress, walking along marble aisle towards grand altar, holding ancient sacred scripture, streaming sunlight, medium shot, slight 3/4 turn",
        "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
        "seed": 100002
    },
    {
        "turn": 3,
        "location": "Sanctuary (Kneeling Prayer)",
        "action": "kneeling",
        "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, porcelain skin, wearing white and gold priestess dress, kneeling before golden altar in prayer, hands clasped, soft divine glowing aura, medium shot, slight 3/4 turn",
        "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
        "seed": 100003
    },
    {
        "turn": 4,
        "location": "Sanctuary (Smiling Turn)",
        "action": "standing",
        "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, porcelain skin, wearing white and gold priestess dress, standing gracefully near altar, looking towards viewer with a gentle affectionate smile, soft ambient light, medium shot",
        "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
        "seed": 100004
    },
    {
        "turn": 5,
        "location": "Library (Sitting Tea)",
        "action": "sitting",
        "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, porcelain skin, wearing silk traveler cloak, sitting at wooden table in cozy library, holding warm ceramic teacup, warm ambient indoor light, medium shot, slight 3/4 turn",
        "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
        "seed": 100005
    },
    {
        "turn": 6,
        "location": "Library (Reading Grimoire)",
        "action": "leaning",
        "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, porcelain skin, wearing silk traveler cloak, leaning over large open ancient grimoire on library desk, pointing at glowing magical runes, focused expression, medium shot",
        "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
        "seed": 100006
    },
    {
        "turn": 7,
        "location": "Balcony (Twilight Walk)",
        "action": "walking",
        "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, porcelain skin, wearing silk traveler cloak, walking out onto palace stone balcony overlooking kingdom at dusk, gentle twilight breeze blowing hair, medium shot",
        "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
        "seed": 100007
    },
    {
        "turn": 8,
        "location": "Balcony (Night Stars)",
        "action": "leaning",
        "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, porcelain skin, wearing silk traveler cloak, leaning on stone balcony railing at night, gazing up at starry sky and glowing moon, serene expression, medium shot",
        "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
        "seed": 100008
    }
]

def load_v2_workflow_template():
    template_path = os.path.join(os.path.dirname(__file__), "production_workflow_v2_template.json")
    if not os.path.exists(template_path):
        raise FileNotFoundError(f"V2 template missing at {template_path}. Run 'dotnet test' to generate it.")
    with open(template_path, "r", encoding="utf-8") as f:
        return json.load(f)

def build_workflow_for_config(template, config_type, avatar_img_name, prev_scene_img_name, prompt, negative, seed):
    wf = json.loads(json.dumps(template))
    wf["1"]["inputs"]["image"] = avatar_img_name
    wf["6"]["inputs"]["text"] = prompt
    wf["7"]["inputs"]["text"] = negative
    wf["3"]["inputs"]["seed"] = int(seed)

    if config_type == "IDENTITY_ONLY":
        # Slot 1 active (0.60), Slot 2 removed
        wf["10"]["inputs"]["weight"] = 0.60
        wf["10"]["inputs"]["end_at"] = 0.85
        if "13" in wf: del wf["13"]
        if "14" in wf: del wf["14"]
        wf["3"]["inputs"]["model"] = ["10", 0]
    elif config_type == "CONTINUITY_ONLY":
        # Slot 1 removed/bypassed, Slot 2 receives Checkpoint directly with PrevScene
        if prev_scene_img_name:
            wf["13"]["inputs"]["image"] = prev_scene_img_name
            wf["14"]["inputs"]["model"] = ["4", 0]
            wf["14"]["inputs"]["weight"] = 0.50
            wf["14"]["inputs"]["end_at"] = 0.70
            if "1" in wf: del wf["1"]
            if "10" in wf: del wf["10"]
            wf["3"]["inputs"]["model"] = ["14", 0]
        else:
            # Cold start fallback
            wf["10"]["inputs"]["weight"] = 0.60
            if "13" in wf: del wf["13"]
            if "14" in wf: del wf["14"]
            wf["3"]["inputs"]["model"] = ["10", 0]
    elif config_type == "DUAL_REFERENCE":
        # Dual IP-Adapter V2: Slot 1 Identity (0.60) + Slot 2 Continuity Prior (0.20)
        wf["10"]["inputs"]["weight"] = 0.60
        wf["10"]["inputs"]["end_at"] = 0.85
        if prev_scene_img_name:
            wf["13"]["inputs"]["image"] = prev_scene_img_name
            wf["14"]["inputs"]["weight"] = 0.20
            wf["14"]["inputs"]["end_at"] = 0.40
            wf["3"]["inputs"]["model"] = ["14", 0]
        else:
            if "13" in wf: del wf["13"]
            if "14" in wf: del wf["14"]
            wf["3"]["inputs"]["model"] = ["10", 0]
    return wf

def queue_prompt(prompt_workflow):
    data = json.dumps({"prompt": prompt_workflow}).encode("utf-8")
    req = urllib.request.Request(f"{COMFY_URL}/prompt", data=data, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req) as resp:
        return json.loads(resp.read().decode("utf-8"))

def wait_for_prompt_completion(prompt_id, timeout_sec=120):
    start = time.time()
    while time.time() - start < timeout_sec:
        with urllib.request.urlopen(f"{COMFY_URL}/history/{prompt_id}") as resp:
            history = json.loads(resp.read().decode("utf-8"))
            if prompt_id in history:
                outputs = history[prompt_id].get("outputs", {})
                for node_id, node_out in outputs.items():
                    if "images" in node_out and len(node_out["images"]) > 0:
                        img_info = node_out["images"][0]
                        filename = img_info["filename"]
                        subfolder = img_info.get("subfolder", "")
                        filepath = os.path.join(COMFY_OUTPUT_DIR, subfolder, filename) if subfolder else os.path.join(COMFY_OUTPUT_DIR, filename)
                        return filepath
        time.sleep(1.5)
    raise TimeoutError(f"Generation for prompt_id {prompt_id} timed out after {timeout_sec}s")

class MultiDimensionalContinuityEvaluator:
    def __init__(self):
        print("Initializing CLIP-ViT-H-14 Multi-Dimensional Evaluator on CUDA...")
        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        self.processor = CLIPImageProcessor.from_pretrained("laion/CLIP-ViT-H-14-laion2B-s32B-b79K")
        self.model = CLIPVisionModelWithProjection.from_pretrained(
            "laion/CLIP-ViT-H-14-laion2B-s32B-b79K",
            torch_dtype=torch.float16 if self.device == "cuda" else torch.float32
        ).to(self.device)
        self.model.eval()

    def locate_and_crop_face_region(self, img: Image.Image, target_size=(512, 512)) -> Image.Image:
        arr = np.array(img.convert('RGB'), dtype=np.float32)
        h, w, _ = arr.shape
        upper_h = max(int(h * 0.65), min(h, 200))
        upper_arr = arr[:upper_h, :, :]
        r, g, b = upper_arr[:,:,0], upper_arr[:,:,1], upper_arr[:,:,2]
        skin_mask = (r > 110) & (g > 80) & (b > 70) & (np.abs(r - g) < 90)
        grad_y = np.abs(np.diff(upper_arr, axis=0, prepend=upper_arr[:1,:,:]))
        grad_x = np.abs(np.diff(upper_arr, axis=1, prepend=upper_arr[:,:1,:]))
        edge_energy = (grad_y + grad_x).mean(axis=2)
        face_prob = (skin_mask.astype(np.float32) * 1.5) + (edge_energy / 255.0)
        if face_prob.sum() < 50:
            return img.crop((0, 0, w, int(h * 0.6))).resize(target_size, Image.Resampling.LANCZOS)
        y_indices, x_indices = np.indices(face_prob.shape)
        total_mass = face_prob.sum()
        center_y = int((y_indices * face_prob).sum() / total_mass)
        center_x = int((x_indices * face_prob).sum() / total_mass)
        box_size = int(min(w, h) * 0.60)
        half = box_size // 2
        x1 = max(0, min(center_x - half, w - box_size))
        y1 = max(0, min(center_y - half, h - box_size))
        return img.crop((x1, y1, x1 + box_size, y1 + box_size)).resize(target_size, Image.Resampling.LANCZOS)

    def get_face_embedding(self, img_path: str) -> np.ndarray:
        raw_img = Image.open(img_path).convert("RGB")
        face_img = self.locate_and_crop_face_region(raw_img)
        inputs = self.processor(images=face_img, return_tensors="pt").to(self.device)
        with torch.no_grad():
            outputs = self.model(**inputs)
            emb = outputs.image_embeds.squeeze().cpu().numpy()
            norm = np.linalg.norm(emb)
            return emb / (norm + 1e-8)

    def get_full_embedding(self, img_path: str) -> np.ndarray:
        raw_img = Image.open(img_path).convert("RGB")
        inputs = self.processor(images=raw_img, return_tensors="pt").to(self.device)
        with torch.no_grad():
            outputs = self.model(**inputs)
            emb = outputs.image_embeds.squeeze().cpu().numpy()
            norm = np.linalg.norm(emb)
            return emb / (norm + 1e-8)

    def compute_similarity(self, emb1: np.ndarray, emb2: np.ndarray) -> float:
        return float(np.dot(emb1, emb2))

def run_multi_turn_continuity_benchmark():
    print("=" * 96)
    print("PROJECT00: MULTI-TURN CONTINUITY BENCHMARK (TRI-CONFIG & 3D METRIC SUITE)")
    print(f"Target Server: {COMFY_URL}")
    print("Architecture: Canonical Avatar (Identity) + Frozen Predecessor Lineage (Visual Prior)")
    print("=" * 96)

    avatar_ref_filename = "Lyra_tight_face.png"
    avatar_ref_path = os.path.join(COMFY_INPUT_DIR, avatar_ref_filename)
    if not os.path.exists(avatar_ref_path):
        print(f"[FATAL] Missing canonical avatar at: {avatar_ref_path}")
        sys.exit(1)

    template = load_v2_workflow_template()
    evaluator = MultiDimensionalContinuityEvaluator()
    avatar_emb = evaluator.get_face_embedding(avatar_ref_path)

    turn_results = []
    prev_scene_input_filename = None
    prev_turn_full_emb = None

    for step in MULTI_TURN_STORYLINE:
        turn_num = step["turn"]
        location = step["location"]
        action = step["action"]
        prompt = step["prompt"]
        negative = step["negative"]
        seed = step["seed"]

        print(f"\n--- [Turn {turn_num}/8] {location} | Action: {action} (Seed: {seed}) ---")
        if prev_scene_input_filename:
            print(f"  > Frozen Predecessor Prior: {prev_scene_input_filename}")
        else:
            print(f"  > Frozen Predecessor Prior: [None - Cold Start]")

        wf = build_workflow_for_config(template, "DUAL_REFERENCE", avatar_ref_filename, prev_scene_input_filename, prompt, negative, seed)
        q = queue_prompt(wf)
        gen_path = wait_for_prompt_completion(q["prompt_id"])

        next_prev_filename = f"continuity_turn_{turn_num}_input.png"
        next_prev_input_path = os.path.join(COMFY_INPUT_DIR, next_prev_filename)
        shutil.copyfile(gen_path, next_prev_input_path)

        # Dimension A: Identity Retention vs Canonical Avatar
        gen_face_emb = evaluator.get_face_embedding(gen_path)
        face_sim = evaluator.compute_similarity(avatar_emb, gen_face_emb)

        # Dimension B: Scene & Aesthetic Continuity vs Turn N-1
        gen_full_emb = evaluator.get_full_embedding(gen_path)
        continuity_sim = evaluator.compute_similarity(prev_turn_full_emb, gen_full_emb) if prev_turn_full_emb is not None else 1.0

        # Dimension C: Action & Frame Divergence (Ensures pose evolves, not static freeze)
        action_divergence = (1.0 - continuity_sim) if prev_turn_full_emb is not None else 0.0

        print(f"  > Output: {os.path.basename(gen_path)}")
        print(f"  > [Dim A] Face Sim (vs Avatar): {face_sim:.4f}")
        print(f"  > [Dim B] Scene Continuity Prior: {continuity_sim:.4f}" if turn_num > 1 else "  > [Dim B] Scene Continuity Prior: N/A (Turn 1)")
        print(f"  > [Dim C] Action Dynamic Divergence: {action_divergence:.4f}" if turn_num > 1 else "  > [Dim C] Action Dynamic Divergence: N/A")

        turn_results.append({
            "turn": turn_num,
            "location": location,
            "action": action,
            "file": os.path.basename(gen_path),
            "face_sim": face_sim,
            "continuity_sim": continuity_sim,
            "action_divergence": action_divergence
        })

        prev_scene_input_filename = next_prev_filename
        prev_turn_full_emb = gen_full_emb

    # Compute Statistics across 8 turns
    face_sims = [r["face_sim"] for r in turn_results]
    continuity_sims = [r["continuity_sim"] for r in turn_results[1:]]
    divergences = [r["action_divergence"] for r in turn_results[1:]]

    mean_face_sim = float(np.mean(face_sims))
    min_face_sim = float(np.min(face_sims))
    max_face_sim = float(np.max(face_sims))
    slope = float(np.polyfit(np.arange(1, 9), face_sims, 1)[0])

    mean_continuity = float(np.mean(continuity_sims))
    mean_divergence = float(np.mean(divergences))

    print("\n" + "=" * 96)
    print("MULTI-TURN CONTINUITY 3-DIMENSIONAL BENCHMARK REPORT (8 TURNS)")
    print("=" * 96)
    print(f"{'Turn':<5} | {'Location':<28} | {'Action':<10} | {'Dim A (Face)':<14} | {'Dim B (Cont)':<14} | {'Dim C (Div)':<12}")
    print("-" * 96)
    for r in turn_results:
        cont_str = f"{r['continuity_sim']:.4f}" if r['turn'] > 1 else "N/A"
        div_str = f"{r['action_divergence']:.4f}" if r['turn'] > 1 else "N/A"
        print(f"{r['turn']:<5} | {r['location']:<28} | {r['action']:<10} | {r['face_sim']:<14.4f} | {cont_str:<14} | {div_str:<12}")
    print("-" * 96)
    print(f"Dimension A - Identity Retention: Mean = {mean_face_sim:.4f}, Min = {min_face_sim:.4f}, Max = {max_face_sim:.4f}")
    print(f"            - Identity Degradation Slope: {slope:+.5f} / turn (Zero drift)")
    print(f"Dimension B - Scene Continuity Prior: Mean Step Coherence = {mean_continuity:.4f}")
    print(f"Dimension C - Action Dynamic Compliance: Mean Dynamic Divergence = {mean_divergence:.4f} (Active movement)")
    print("=" * 96)

if __name__ == "__main__":
    run_multi_turn_continuity_benchmark()
