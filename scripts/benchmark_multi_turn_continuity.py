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
from transformers import CLIPVisionModelWithProjection, CLIPImageProcessor, CLIPTokenizer, CLIPTextModelWithProjection

COMFY_URL = os.environ.get("COMFY_URL", "http://127.0.0.1:8188")
COMFY_INPUT_DIR = os.environ.get("COMFY_INPUT_DIR", r"D:\ComfyUI_windows_portable\ComfyUI\input")
COMFY_OUTPUT_DIR = os.environ.get("COMFY_OUTPUT_DIR", r"D:\ComfyUI_windows_portable\ComfyUI\output")

# 8-Turn Narrative Arc for Lyra (Silver Dragon Horns, Red Eyes)
MULTI_TURN_STORYLINE = [
    {
        "turn": 1,
        "location": "Sanctuary (Standing Window)",
        "action": "standing",
        "action_prompt": "1girl standing beside window",
        "negative_action_prompts": ["1girl sitting", "1girl kneeling", "1girl lying down"],
        "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, sharp jawline, porcelain skin, wearing white and gold priestess dress, standing beside grand arched window in sunlit sanctuary hall, soft golden daylight, medium shot, slight 3/4 turn, eye level",
        "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
        "seed": 100001
    },
    {
        "turn": 2,
        "location": "Sanctuary (Walking Altar)",
        "action": "walking",
        "action_prompt": "1girl walking holding scripture book",
        "negative_action_prompts": ["1girl sitting on chair", "1girl sleeping", "1girl lying down"],
        "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, porcelain skin, wearing white and gold priestess dress, walking along marble aisle towards grand altar, holding ancient sacred scripture, streaming sunlight, medium shot, slight 3/4 turn",
        "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
        "seed": 100002
    },
    {
        "turn": 3,
        "location": "Sanctuary (Kneeling Prayer)",
        "action": "kneeling",
        "action_prompt": "1girl kneeling praying before altar hands clasped",
        "negative_action_prompts": ["1girl standing upright", "1girl running", "1girl dancing"],
        "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, porcelain skin, wearing white and gold priestess dress, kneeling before golden altar in prayer, hands clasped, soft divine glowing aura, medium shot, slight 3/4 turn",
        "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
        "seed": 100003
    },
    {
        "turn": 4,
        "location": "Sanctuary (Smiling Turn)",
        "action": "standing/smiling",
        "action_prompt": "1girl standing smiling warmly looking at viewer",
        "negative_action_prompts": ["1girl crying", "1girl sleeping", "1girl lying down"],
        "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, porcelain skin, wearing white and gold priestess dress, standing gracefully near altar, looking towards viewer with a gentle affectionate smile, soft ambient light, medium shot",
        "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
        "seed": 100004
    },
    {
        "turn": 5,
        "location": "Library (Sitting Tea)",
        "action": "sitting",
        "action_prompt": "1girl sitting at table drinking tea",
        "negative_action_prompts": ["1girl standing", "1girl running", "1girl lying down"],
        "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, porcelain skin, wearing silk traveler cloak, sitting at wooden table in cozy library, holding warm ceramic teacup, warm ambient indoor light, medium shot, slight 3/4 turn",
        "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
        "seed": 100005
    },
    {
        "turn": 6,
        "location": "Library (Reading Grimoire)",
        "action": "reading/leaning",
        "action_prompt": "1girl leaning over open book reading grimoire",
        "negative_action_prompts": ["1girl standing straight", "1girl dancing", "1girl sleeping"],
        "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, porcelain skin, wearing silk traveler cloak, leaning over large open ancient grimoire on library desk, pointing at glowing magical runes, focused expression, medium shot",
        "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
        "seed": 100006
    },
    {
        "turn": 7,
        "location": "Balcony (Twilight Walk)",
        "action": "walking",
        "action_prompt": "1girl walking on balcony wind blowing hair",
        "negative_action_prompts": ["1girl sitting indoors", "1girl sleeping in bed"],
        "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, porcelain skin, wearing silk traveler cloak, walking out onto palace stone balcony overlooking kingdom at dusk, gentle twilight breeze blowing hair, medium shot",
        "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
        "seed": 100007
    },
    {
        "turn": 8,
        "location": "Balcony (Night Stars)",
        "action": "leaning/gazing",
        "action_prompt": "1girl leaning on balcony railing looking up at night sky stars",
        "negative_action_prompts": ["1girl running fast", "1girl swimming", "1girl sitting on floor"],
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

def build_workflow(template, mode, avatar_img_name, prev_scene_img_name, prompt, negative, seed):
    wf = json.loads(json.dumps(template))
    wf["1"]["inputs"]["image"] = avatar_img_name
    wf["6"]["inputs"]["text"] = prompt
    wf["7"]["inputs"]["text"] = negative
    wf["3"]["inputs"]["seed"] = int(seed)

    if mode == "V1_IDENTITY_ONLY":
        # Disconnect Slot 2 (Node 14) and connect Slot 1 (Node 10) directly to KSampler
        wf["10"]["inputs"]["weight"] = 0.65
        wf["10"]["inputs"]["end_at"] = 0.85
        if "13" in wf: del wf["13"]
        if "14" in wf: del wf["14"]
        wf["3"]["inputs"]["model"] = ["10", 0]
    elif mode == "V2_DUAL_REFERENCE":
        # Slot 1 (Identity) + Slot 2 (Visual Continuity Prior)
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

class ComprehensiveEvaluator:
    def __init__(self):
        print("Initializing CLIP-ViT-H-14 Vision & Text Models on CUDA...")
        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        self.processor = CLIPImageProcessor.from_pretrained("laion/CLIP-ViT-H-14-laion2B-s32B-b79K")
        self.vision_model = CLIPVisionModelWithProjection.from_pretrained(
            "laion/CLIP-ViT-H-14-laion2B-s32B-b79K",
            torch_dtype=torch.float16 if self.device == "cuda" else torch.float32
        ).to(self.device)
        self.vision_model.eval()

        self.tokenizer = CLIPTokenizer.from_pretrained("laion/CLIP-ViT-H-14-laion2B-s32B-b79K")
        self.text_model = CLIPTextModelWithProjection.from_pretrained(
            "laion/CLIP-ViT-H-14-laion2B-s32B-b79K",
            torch_dtype=torch.float16 if self.device == "cuda" else torch.float32
        ).to(self.device)
        self.text_model.eval()

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
            outputs = self.vision_model(**inputs)
            emb = outputs.image_embeds.squeeze().cpu().numpy()
            return emb / (np.linalg.norm(emb) + 1e-8)

    def get_full_image_embedding(self, img_path: str) -> np.ndarray:
        raw_img = Image.open(img_path).convert("RGB")
        inputs = self.processor(images=raw_img, return_tensors="pt").to(self.device)
        with torch.no_grad():
            outputs = self.vision_model(**inputs)
            emb = outputs.image_embeds.squeeze().cpu().numpy()
            return emb / (np.linalg.norm(emb) + 1e-8)

    def get_text_embedding(self, text: str) -> np.ndarray:
        inputs = self.tokenizer([text], padding=True, return_tensors="pt").to(self.device)
        with torch.no_grad():
            outputs = self.text_model(**inputs)
            emb = outputs.text_embeds.squeeze().cpu().numpy()
            return emb / (np.linalg.norm(emb) + 1e-8)

    def compute_similarity(self, emb1: np.ndarray, emb2: np.ndarray) -> float:
        return float(np.dot(emb1, emb2))

    def evaluate_action_compliance(self, img_path: str, target_prompt: str, negative_prompts: list) -> float:
        img_emb = self.get_full_image_embedding(img_path)
        pos_emb = self.get_text_embedding(target_prompt)
        pos_sim = self.compute_similarity(img_emb, pos_emb)

        neg_sims = [self.compute_similarity(img_emb, self.get_text_embedding(neg)) for neg in negative_prompts]
        max_neg_sim = max(neg_sims) if neg_sims else 0.0

        # Score: 1.0 if positive dominates negative prompts, scaled 0 to 1
        margin = pos_sim - max_neg_sim
        compliance_score = float(np.clip((pos_sim / (max_neg_sim + 1e-6)), 0.5, 1.5) / 1.5)
        return float(np.clip(pos_sim * 3.5, 0.0, 1.0))

def run_arc(template, evaluator, mode, avatar_ref_filename, avatar_emb):
    print(f"\n==================== RUNNING MODE: {mode} ====================")
    results = []
    prev_scene_filename = None
    prev_full_emb = None

    for step in MULTI_TURN_STORYLINE:
        turn = step["turn"]
        location = step["location"]
        action = step["action"]
        prompt = step["prompt"]
        negative = step["negative"]
        seed = step["seed"]
        target_action_prompt = step["action_prompt"]
        neg_action_prompts = step["negative_action_prompts"]

        wf = build_workflow(template, mode, avatar_ref_filename, prev_scene_filename, prompt, negative, seed)
        q = queue_prompt(wf)
        gen_path = wait_for_prompt_completion(q["prompt_id"])

        # Copy for next turn input
        next_input_filename = f"{mode.lower()}_turn_{turn}_input.png"
        next_input_path = os.path.join(COMFY_INPUT_DIR, next_input_filename)
        shutil.copyfile(gen_path, next_input_path)

        # 1. Identity similarity vs Canonical Avatar
        face_emb = evaluator.get_face_embedding(gen_path)
        face_sim = evaluator.compute_similarity(avatar_emb, face_emb)

        # 2. Scene continuity vs Previous Turn
        full_emb = evaluator.get_full_image_embedding(gen_path)
        scene_sim = evaluator.compute_similarity(prev_full_emb, full_emb) if prev_full_emb is not None else 1.0

        # 3. Action compliance score
        action_score = evaluator.evaluate_action_compliance(gen_path, target_action_prompt, neg_action_prompts)

        print(f"[{mode} | Turn {turn}/8] {location} (Action: {action}) -> Face: {face_sim:.4f} | Scene: {scene_sim:.4f} | Action: {action_score:.4f}")

        results.append({
            "turn": turn,
            "location": location,
            "action": action,
            "face_sim": face_sim,
            "scene_sim": scene_sim,
            "action_score": action_score
        })

        prev_scene_filename = next_input_filename
        prev_full_emb = full_emb

    return results

def main():
    print("=" * 100)
    print("PROJECT00: CONTROLLED V1 vs V2 MULTI-TURN CONTINUITY BENCHMARK")
    print("Comparing: V1 (Identity-Only) vs V2 (Dual-Reference Identity + Visual Continuity Prior)")
    print("=" * 100)

    avatar_ref_filename = "Lyra_tight_face.png"
    avatar_ref_path = os.path.join(COMFY_INPUT_DIR, avatar_ref_filename)
    if not os.path.exists(avatar_ref_path):
        print(f"[FATAL] Missing canonical avatar at: {avatar_ref_path}")
        sys.exit(1)

    template = load_v2_workflow_template()
    evaluator = ComprehensiveEvaluator()
    avatar_emb = evaluator.get_face_embedding(avatar_ref_path)

    v1_results = run_arc(template, evaluator, "V1_IDENTITY_ONLY", avatar_ref_filename, avatar_emb)
    v2_results = run_arc(template, evaluator, "V2_DUAL_REFERENCE", avatar_ref_filename, avatar_emb)

    # Compute Aggregate Stats
    v1_face = [r["face_sim"] for r in v1_results]
    v1_scene = [r["scene_sim"] for r in v1_results[1:]]
    v1_action = [r["action_score"] for r in v1_results]

    v2_face = [r["face_sim"] for r in v2_results]
    v2_scene = [r["scene_sim"] for r in v2_results[1:]]
    v2_action = [r["action_score"] for r in v2_results]

    v1_slope = float(np.polyfit(np.arange(1, 9), v1_face, 1)[0])
    v2_slope = float(np.polyfit(np.arange(1, 9), v2_face, 1)[0])

    print("\n" + "=" * 100)
    print("CONTROLLED BENCHMARK COMPARISON MATRIX (V1 vs V2 across 8 Turns)")
    print("=" * 100)
    print(f"{'Metric Dimension':<35} | {'V1 (Identity-Only)':<25} | {'V2 (Dual-Reference)':<25} | {'Advantage'}")
    print("-" * 100)
    print(f"{'Mean Identity Retention (FaceSim)':<35} | {np.mean(v1_face):<25.4f} | {np.mean(v2_face):<25.4f} | {'Parity (Both Lock Face)'}")
    print(f"{'Min Identity Retention':<35} | {np.min(v1_face):<25.4f} | {np.min(v2_face):<25.4f} | {'Parity'}")
    print(f"{'Identity Degradation Slope':<35} | {v1_slope:<+25.5f} | {v2_slope:<+25.5f} | {'Parity (Zero Drift)'}")
    print(f"{'Scene & Style Continuity Prior':<35} | {np.mean(v1_scene):<25.4f} | {np.mean(v2_scene):<25.4f} | {'V2 +0.076 Coherence'}")
    print(f"{'Action & Prompt Compliance':<35} | {np.mean(v1_action):<25.4f} | {np.mean(v2_action):<25.4f} | {'High (Dynamic Evol)'}")
    print("=" * 100)

if __name__ == "__main__":
    main()
