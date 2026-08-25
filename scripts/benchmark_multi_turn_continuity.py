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

PROVISIONAL_ACCEPTANCE_THRESHOLD = 0.75

# 8-Turn Narrative Arc for Lyra (Silver Dragon Horns, Red Eyes)
MULTI_TURN_STORYLINE = [
    {
        "turn": 1,
        "location": "Sanctuary (Standing Window)",
        "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, sharp jawline, porcelain skin, wearing white and gold priestess dress, standing beside grand arched window in sunlit sanctuary hall, soft golden daylight, medium shot, slight 3/4 turn, eye level",
        "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
        "seed": 100001
    },
    {
        "turn": 2,
        "location": "Sanctuary (Walking Altar)",
        "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, porcelain skin, wearing white and gold priestess dress, walking along marble aisle towards grand altar, holding ancient sacred scripture, streaming sunlight, medium shot, slight 3/4 turn",
        "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
        "seed": 100002
    },
    {
        "turn": 3,
        "location": "Sanctuary (Kneeling Prayer)",
        "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, porcelain skin, wearing white and gold priestess dress, kneeling before golden altar in prayer, hands clasped, soft divine glowing aura, medium shot, slight 3/4 turn",
        "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
        "seed": 100003
    },
    {
        "turn": 4,
        "location": "Sanctuary (Smiling Turn)",
        "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, porcelain skin, wearing white and gold priestess dress, standing gracefully near altar, looking towards viewer with a gentle affectionate smile, soft ambient light, medium shot",
        "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
        "seed": 100004
    },
    {
        "turn": 5,
        "location": "Library (Sitting Tea)",
        "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, porcelain skin, wearing silk traveler cloak, sitting at wooden table in cozy library, holding warm ceramic teacup, warm ambient indoor light, medium shot, slight 3/4 turn",
        "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
        "seed": 100005
    },
    {
        "turn": 6,
        "location": "Library (Reading Grimoire)",
        "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, porcelain skin, wearing silk traveler cloak, leaning over large open ancient grimoire on library desk, pointing at glowing magical runes, focused expression, medium shot",
        "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
        "seed": 100006
    },
    {
        "turn": 7,
        "location": "Balcony (Twilight Walk)",
        "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, porcelain skin, wearing silk traveler cloak, walking out onto palace stone balcony overlooking kingdom at dusk, gentle twilight breeze blowing hair, medium shot",
        "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
        "seed": 100007
    },
    {
        "turn": 8,
        "location": "Balcony (Night Stars)",
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

def build_v2_workflow(template, avatar_img_name, prev_scene_img_name, prompt, negative, seed, ip_weight=0.60, ip_end_at=0.85, scene_weight=0.20, scene_end_at=0.40):
    wf = json.loads(json.dumps(template))
    wf["1"]["inputs"]["image"] = avatar_img_name
    wf["6"]["inputs"]["text"] = prompt
    wf["7"]["inputs"]["text"] = negative
    wf["3"]["inputs"]["seed"] = int(seed)
    wf["10"]["inputs"]["weight"] = float(ip_weight)
    wf["10"]["inputs"]["end_at"] = float(ip_end_at)
    
    if prev_scene_img_name:
        wf["13"]["inputs"]["image"] = prev_scene_img_name
        wf["14"]["inputs"]["weight"] = float(scene_weight)
        wf["14"]["inputs"]["end_at"] = float(scene_end_at)
        wf["3"]["inputs"]["model"] = ["14", 0]
    else:
        # Fallback for Turn 1: disconnect Node 14 and link Node 10 directly
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

class VisualContinuityEvaluator:
    def __init__(self):
        print("Initializing CLIP-ViT-H-14 Vision Evaluator on CUDA...")
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
            raise RuntimeError("Facial region localization failed.")
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

    def get_full_image_embedding(self, img_path: str) -> np.ndarray:
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
    print("=" * 90)
    print("PROJECT00: MULTI-TURN VISUAL CONTINUITY GPU BENCHMARK (PR #21)")
    print(f"Target Server: {COMFY_URL}")
    print(f"Workflow: VisualContinuity V2 (Dual IP-Adapter: Identity 0.60 + Previous Scene 0.20)")
    print(f"Provisional Acceptance Threshold: {PROVISIONAL_ACCEPTANCE_THRESHOLD}")
    print("=" * 90)

    avatar_ref_filename = "Lyra_tight_face.png"
    avatar_ref_path = os.path.join(COMFY_INPUT_DIR, avatar_ref_filename)
    if not os.path.exists(avatar_ref_path):
        print(f"[FATAL] Missing canonical avatar at: {avatar_ref_path}")
        sys.exit(1)

    template = load_v2_workflow_template()
    evaluator = VisualContinuityEvaluator()
    avatar_emb = evaluator.get_face_embedding(avatar_ref_path)

    turn_results = []
    prev_scene_input_filename = None
    prev_turn_full_emb = None

    for step in MULTI_TURN_STORYLINE:
        turn_num = step["turn"]
        location = step["location"]
        prompt = step["prompt"]
        negative = step["negative"]
        seed = step["seed"]

        print(f"\n--- [Turn {turn_num}/8] {location} (Seed: {seed}) ---")
        if prev_scene_input_filename:
            print(f"  > Continuity Reference (Slot 2): {prev_scene_input_filename}")
        else:
            print(f"  > Continuity Reference (Slot 2): [None - Cold Start]")

        wf = build_v2_workflow(template, avatar_ref_filename, prev_scene_input_filename, prompt, negative, seed)
        q = queue_prompt(wf)
        gen_path = wait_for_prompt_completion(q["prompt_id"])
        
        # Copy generated image into ComfyUI input directory for the next turn's Slot 2 conditioning!
        next_prev_filename = f"continuity_turn_{turn_num}_input.png"
        next_prev_input_path = os.path.join(COMFY_INPUT_DIR, next_prev_filename)
        shutil.copyfile(gen_path, next_prev_input_path)

        # 1. Measure Identity Similarity against Canonical Avatar
        gen_face_emb = evaluator.get_face_embedding(gen_path)
        face_sim = evaluator.compute_similarity(avatar_emb, gen_face_emb)

        # 2. Measure Step-to-Step Full Frame Aesthetic Continuity
        gen_full_emb = evaluator.get_full_image_embedding(gen_path)
        step_sim = evaluator.compute_similarity(prev_turn_full_emb, gen_full_emb) if prev_turn_full_emb is not None else 1.0

        status = "PASS" if face_sim >= PROVISIONAL_ACCEPTANCE_THRESHOLD else "BELOW_THRESHOLD"
        print(f"  > Output: {os.path.basename(gen_path)}")
        print(f"  > Identity Sim (vs Avatar): {face_sim:.4f} | Step Sim (vs Turn N-1): {step_sim:.4f} | Status: {status}")

        turn_results.append({
            "turn": turn_num,
            "location": location,
            "file": os.path.basename(gen_path),
            "face_sim": face_sim,
            "step_sim": step_sim,
            "status": status
        })

        # Update pointers for next turn
        prev_scene_input_filename = next_prev_filename
        prev_turn_full_emb = gen_full_emb

    # Summary Report
    all_face_sims = [r["face_sim"] for r in turn_results]
    all_step_sims = [r["step_sim"] for r in turn_results[1:]] # exclude turn 1 self-sim
    mean_face_sim = float(np.mean(all_face_sims))
    min_face_sim = float(np.min(all_face_sims))
    mean_step_sim = float(np.mean(all_step_sims))
    pass_rate = (sum(1 for s in all_face_sims if s >= PROVISIONAL_ACCEPTANCE_THRESHOLD) / len(all_face_sims)) * 100.0

    print("\n" + "=" * 90)
    print("MULTI-TURN CONTINUITY BENCHMARK SUMMARY (8 TURNS CHAIN)")
    print("=" * 90)
    print(f"{'Turn':<6} | {'Location':<28} | {'Face Sim (Avatar)':<18} | {'Step Sim (Prev)':<16} | {'Status':<8}")
    print("-" * 90)
    for r in turn_results:
        step_str = f"{r['step_sim']:.4f}" if r['turn'] > 1 else "N/A (Turn 1)"
        print(f"{r['turn']:<6} | {r['location']:<28} | {r['face_sim']:<18.4f} | {step_str:<16} | {r['status']:<8}")
    print("-" * 90)
    print(f"Overall Mean Identity Sim: {mean_face_sim:.4f} (Min: {min_face_sim:.4f})")
    print(f"Overall Mean Step Continuity: {mean_step_sim:.4f}")
    print(f"Pass Rate (>= {PROVISIONAL_ACCEPTANCE_THRESHOLD}): {pass_rate:.1f}%")
    print("=" * 90)

if __name__ == "__main__":
    run_multi_turn_continuity_benchmark()
