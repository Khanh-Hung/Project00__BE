import os
import sys
import json
import time
import urllib.request
import urllib.parse
import torch
import numpy as np
from PIL import Image
from transformers import CLIPVisionModelWithProjection, CLIPImageProcessor

# Configurable environment variables with local defaults
COMFY_URL = os.environ.get("COMFY_URL", "http://127.0.0.1:8188")
COMFY_INPUT_DIR = os.environ.get("COMFY_INPUT_DIR", r"D:\ComfyUI_windows_portable\ComfyUI\input")
COMFY_OUTPUT_DIR = os.environ.get("COMFY_OUTPUT_DIR", r"D:\ComfyUI_windows_portable\ComfyUI\output")

# Provisional acceptance threshold for benchmark gate
PROVISIONAL_ACCEPTANCE_THRESHOLD = 0.75

# 3 Distinct Canonical Benchmark Characters with real avatar files in ComfyUI/input
BENCHMARK_CHARACTERS = [
    {
        "id": "char_lyra",
        "name": "Lyra (Silver Dragon Horns & Red Eyes)",
        "reference_image": "Lyra_tight_face.png",
        "scenarios": [
            {
                "name": "Sanctuary (Standing)",
                "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, sharp jawline, porcelain skin, wearing white and gold dress, standing beside arched window in grand sanctuary hall, soft natural daylight, medium shot, slight 3/4 turn, eye level",
                "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, extra digits, cropped, blurry, low quality",
                "seeds": [700001, 700002, 700003]
            },
            {
                "name": "Library (Sitting)",
                "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, porcelain skin, wearing silk traveler cloak, sitting at wooden table in cozy library, holding ceramic teacup, warm ambient indoor light, medium shot, slight 3/4 turn",
                "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seeds": [700004, 700005, 700006]
            }
        ]
    },
    {
        "id": "char_b",
        "name": "Archetype B (Golden Twilight Mage)",
        "reference_image": "13d535721d7e4326ad78e3052f7a3bcd_TextToImage_v1_00020_.png",
        "scenarios": [
            {
                "name": "Observatory (Standing)",
                "prompt": "masterpiece, best quality, solo, 1girl, delicate youthful face, expressive eyes, long flowing hair, wearing elegant magical robe, standing in celestial observatory, starlight illumination, medium shot, slight 3/4 turn",
                "negative": "2girls, multiple people, bad anatomy, bad hands, cropped, blurry, low quality",
                "seeds": [800001, 800002, 800003]
            },
            {
                "name": "Courtyard (Walking)",
                "prompt": "masterpiece, best quality, solo, 1girl, delicate youthful face, expressive eyes, long flowing hair, wearing traveler cloak, walking in stone courtyard near fountain, daylight, medium shot, slight 3/4 turn",
                "negative": "2girls, multiple people, bad anatomy, bad hands, cropped, blurry, low quality",
                "seeds": [800004, 800005, 800006]
            }
        ]
    },
    {
        "id": "char_c",
        "name": "Archetype C (Raven Shadow Knight)",
        "reference_image": "225563c3071a4717b623481cf72a0f32_TextToImage_v1_00021_.png",
        "scenarios": [
            {
                "name": "Armory (Standing)",
                "prompt": "masterpiece, best quality, solo, 1boy, handsome chiseled face, determined jawline, dark spiky hair, wearing dark knight tunic with silver pauldrons, standing in armory hall, torchlight ambient, medium shot, slight 3/4 turn",
                "negative": "2boys, multiple people, bad anatomy, bad hands, cropped, blurry, low quality",
                "seeds": [900001, 900002, 900003]
            },
            {
                "name": "Balcony (Overlooking)",
                "prompt": "masterpiece, best quality, solo, 1boy, handsome chiseled face, dark spiky hair, wearing dark knight tunic, standing on stone balcony overlooking kingdom at dusk, twilight lighting, medium shot, slight 3/4 turn",
                "negative": "2boys, multiple people, bad anatomy, bad hands, cropped, blurry, low quality",
                "seeds": [900004, 900005, 900006]
            }
        ]
    }
]

def load_production_workflow_template():
    template_path = os.path.join(os.path.dirname(__file__), "production_workflow_v1_template.json")
    if not os.path.exists(template_path):
        raise FileNotFoundError(
            f"Production workflow template missing at {template_path}. Run 'dotnet test' to generate it."
        )
    with open(template_path, "r", encoding="utf-8") as f:
        return json.load(f)

def build_workflow_from_production_template(template, reference_img_name, prompt, negative, seed, ip_weight, ip_end_at):
    """
    Constructs execution workflow directly from C# VisualIdentityWorkflowV1Builder export,
    guaranteeing 100% mathematical and node topology parity with production backend.
    """
    wf = json.loads(json.dumps(template))
    wf["1"]["inputs"]["image"] = reference_img_name
    wf["6"]["inputs"]["text"] = prompt
    wf["7"]["inputs"]["text"] = negative
    wf["3"]["inputs"]["seed"] = int(seed)
    wf["10"]["inputs"]["weight"] = float(ip_weight)
    wf["10"]["inputs"]["end_at"] = float(ip_end_at)
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

class FacialRegionLocalizationAndEmbeddingEvaluator:
    def __init__(self):
        print("Initializing CLIP-ViT-H-14 Face-Region Vision Model on CUDA...")
        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        self.processor = CLIPImageProcessor.from_pretrained("laion/CLIP-ViT-H-14-laion2B-s32B-b79K")
        self.model = CLIPVisionModelWithProjection.from_pretrained(
            "laion/CLIP-ViT-H-14-laion2B-s32B-b79K",
            torch_dtype=torch.float16 if self.device == "cuda" else torch.float32
        ).to(self.device)
        self.model.eval()

    def locate_and_crop_face_region(self, img: Image.Image, target_size=(512, 512)) -> Image.Image:
        """
        Facial-region localization heuristic:
        Computes skin luminance & facial contour gradient energy center of mass
        and crops a normalized square face box for consistent embedding comparison.
        """
        arr = np.array(img.convert('RGB'), dtype=np.float32)
        h, w, _ = arr.shape
        
        upper_h = max(int(h * 0.65), min(h, 200))
        upper_arr = arr[:upper_h, :, :]
        
        # Skin tone luminance filter
        r, g, b = upper_arr[:,:,0], upper_arr[:,:,1], upper_arr[:,:,2]
        skin_mask = (r > 110) & (g > 80) & (b > 70) & (np.abs(r - g) < 90)
        
        # Edge gradient energy filter for facial contours (eyes, brows, mouth)
        grad_y = np.abs(np.diff(upper_arr, axis=0, prepend=upper_arr[:1,:,:]))
        grad_x = np.abs(np.diff(upper_arr, axis=1, prepend=upper_arr[:,:1,:]))
        edge_energy = (grad_y + grad_x).mean(axis=2)
        
        face_prob = (skin_mask.astype(np.float32) * 1.5) + (edge_energy / 255.0)
        
        if face_prob.sum() < 50:
            raise RuntimeError("Facial region localization failed: insufficient facial feature energy detected.")
            
        y_indices, x_indices = np.indices(face_prob.shape)
        total_mass = face_prob.sum()
        center_y = int((y_indices * face_prob).sum() / total_mass)
        center_x = int((x_indices * face_prob).sum() / total_mass)
        
        box_size = int(min(w, h) * 0.60)
        half = box_size // 2
        
        x1 = max(0, min(center_x - half, w - box_size))
        y1 = max(0, min(center_y - half, h - box_size))
        x2 = x1 + box_size
        y2 = y1 + box_size
        
        cropped = img.crop((x1, y1, x2, y2))
        return cropped.resize(target_size, Image.Resampling.LANCZOS)

    def get_face_embedding(self, img_path: str) -> np.ndarray:
        if not os.path.exists(img_path):
            raise FileNotFoundError(f"Target image path not found: {img_path}")

        raw_img = Image.open(img_path).convert("RGB")
        face_img = self.locate_and_crop_face_region(raw_img)
        
        inputs = self.processor(images=face_img, return_tensors="pt").to(self.device)
        with torch.no_grad():
            outputs = self.model(**inputs)
            emb = outputs.image_embeds.squeeze().cpu().numpy()
            norm = np.linalg.norm(emb)
            if norm == 0:
                raise RuntimeError(f"Zero vector embedding extracted from {img_path}")
            return emb / norm

    def compute_similarity(self, emb1: np.ndarray, emb2: np.ndarray) -> float:
        return float(np.dot(emb1, emb2))

def run_benchmark():
    print("=" * 88)
    print("PROJECT00: EMPIRICAL FACE-REGION VISUAL IDENTITY GPU BENCHMARK (PHASE 20.1)")
    print(f"Target Server: {COMFY_URL}")
    print(f"Metric: Face-Region Visual Similarity (CLIP-ViT-H-14 normalized cosine)")
    print(f"Provisional Acceptance Threshold: {PROVISIONAL_ACCEPTANCE_THRESHOLD}")
    print("=" * 88)

    evaluator = FacialRegionLocalizationAndEmbeddingEvaluator()
    template = load_production_workflow_template()

    # Verify all reference files exist before launching GPU runs
    for char in BENCHMARK_CHARACTERS:
        ref_path = os.path.join(COMFY_INPUT_DIR, char["reference_image"])
        if not os.path.exists(ref_path):
            print(f"[FATAL] Missing canonical reference image at: {ref_path}")
            sys.exit(1)

    configs = [
        {"name": "Baseline (Weight=0.55, EndAt=0.75)", "weight": 0.55, "end_at": 0.75},
        {"name": "Candidate (Weight=0.65, EndAt=0.85)", "weight": 0.65, "end_at": 0.85}
    ]

    summary_records = []

    for cfg in configs:
        print(f"\n{'#' * 88}")
        print(f"RUNNING CONFIGURATION: {cfg['name']}")
        print(f"{'#' * 88}")

        cfg_all_scores = []

        for char in BENCHMARK_CHARACTERS:
            char_name = char["name"]
            ref_path = os.path.join(COMFY_INPUT_DIR, char["reference_image"])
            ref_emb = evaluator.get_face_embedding(ref_path)

            print(f"\n--- Archetype: {char_name} ---")

            for scn in char["scenarios"]:
                scn_name = scn["name"]
                print(f"  > Scenario: {scn_name}")
                for seed in scn["seeds"]:
                    wf = build_workflow_from_production_template(template, char["reference_image"], scn["prompt"], scn["negative"], seed, cfg["weight"], cfg["end_at"])
                    q = queue_prompt(wf)
                    gen_path = wait_for_prompt_completion(q["prompt_id"])

                    gen_emb = evaluator.get_face_embedding(gen_path)
                    sim = evaluator.compute_similarity(ref_emb, gen_emb)
                    cfg_all_scores.append(sim)

                    status = "PASS" if sim >= PROVISIONAL_ACCEPTANCE_THRESHOLD else "BELOW_THRESHOLD"
                    print(f"    [Seed {seed}] File: {os.path.basename(gen_path)} | Face Sim: {sim:.4f} | Status: {status}")

        mean_sim = float(np.mean(cfg_all_scores))
        min_sim = float(np.min(cfg_all_scores))
        std_sim = float(np.std(cfg_all_scores))
        pass_count = sum(1 for s in cfg_all_scores if s >= PROVISIONAL_ACCEPTANCE_THRESHOLD)
        pass_rate = (pass_count / len(cfg_all_scores)) * 100.0 if cfg_all_scores else 0.0

        summary_records.append({
            "config": cfg["name"],
            "mean": mean_sim,
            "min": min_sim,
            "std": std_sim,
            "pass_rate": pass_rate,
            "samples": len(cfg_all_scores)
        })

    # Compute comparative improvements
    base_mean = summary_records[0]["mean"]
    cand_mean = summary_records[1]["mean"]
    abs_diff = cand_mean - base_mean
    rel_diff = (abs_diff / base_mean) * 100.0 if base_mean > 0 else 0.0

    print("\n" + "=" * 88)
    print("FINAL COMPARISON: BASELINE (0.55/0.75) vs CANDIDATE (0.65/0.85)")
    print("=" * 88)
    print(f"{'Configuration':<42} | {'Mean Sim':<10} | {'Min Sim':<10} | {'StdDev':<10} | {'Pass Rate':<10}")
    print("-" * 88)
    for r in summary_records:
        print(f"{r['config']:<42} | {r['mean']:<10.4f} | {r['min']:<10.4f} | {r['std']:<10.4f} | {r['pass_rate']:<9.1f}%")
    print("-" * 88)
    print(f"Candidate Improvement: +{abs_diff:.4f} absolute cosine similarity (+{rel_diff:.2f}% relative improvement)")
    print(f"Provisional Acceptance Threshold ({PROVISIONAL_ACCEPTANCE_THRESHOLD}): {'ALL PASSED [OK]' if summary_records[1]['pass_rate'] == 100.0 else 'FAILED'}")
    print("=" * 88)

if __name__ == "__main__":
    run_benchmark()
