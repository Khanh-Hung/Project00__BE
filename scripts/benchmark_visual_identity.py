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

COMFY_URL = os.environ.get("COMFY_URL", "http://127.0.0.1:8188")
COMFY_INPUT_DIR = os.environ.get("COMFY_INPUT_DIR", r"D:\ComfyUI_windows_portable\ComfyUI\input")
COMFY_OUTPUT_DIR = os.environ.get("COMFY_OUTPUT_DIR", r"D:\ComfyUI_windows_portable\ComfyUI\output")

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

def build_workflow(reference_img_name, prompt, negative, seed, ip_weight=0.65, ip_end_at=0.85):
    return {
        "1": {
            "class_type": "LoadImage",
            "inputs": { "image": reference_img_name }
        },
        "2": {
            "class_type": "CLIPVisionLoader",
            "inputs": { "clip_name": "CLIP-ViT-H-14-laion2B-s32B-b79K.safetensors" }
        },
        "8": {
            "class_type": "IPAdapterModelLoader",
            "inputs": { "ipadapter_file": "ip-adapter-plus_sd15.safetensors" }
        },
        "10": {
            "class_type": "IPAdapterAdvanced",
            "inputs": {
                "weight": float(ip_weight),
                "weight_type": "linear",
                "combine_embeds": "concat",
                "start_at": 0.0,
                "end_at": float(ip_end_at),
                "embeds_scaling": "K+V",
                "model": ["4", 0],
                "ipadapter": ["8", 0],
                "image": ["1", 0],
                "clip_vision": ["2", 0]
            }
        },
        "4": {
            "class_type": "CheckpointLoaderSimple",
            "inputs": { "ckpt_name": "meinamix_meinaV11.safetensors" }
        },
        "5": {
            "class_type": "EmptyLatentImage",
            "inputs": { "width": 512, "height": 768, "batch_size": 1 }
        },
        "6": {
            "class_type": "CLIPTextEncode",
            "inputs": { "text": prompt, "clip": ["4", 1] }
        },
        "7": {
            "class_type": "CLIPTextEncode",
            "inputs": { "text": negative, "clip": ["4", 1] }
        },
        "3": {
            "class_type": "KSampler",
            "inputs": {
                "seed": int(seed),
                "steps": 28,
                "cfg": 7.0,
                "sampler_name": "euler_ancestral",
                "scheduler": "karras",
                "denoise": 1.0,
                "model": ["10", 0],
                "positive": ["6", 0],
                "negative": ["7", 0],
                "latent_image": ["5", 0]
            }
        },
        "9": {
            "class_type": "VAEDecode",
            "inputs": { "samples": ["3", 0], "vae": ["4", 2] }
        },
        "12": {
            "class_type": "SaveImage",
            "inputs": { "filename_prefix": f"Benchmark_FaceID_{seed}", "images": ["9", 0] }
        }
    }

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

class FaceIdentityEvaluator:
    def __init__(self):
        print("Loading CLIP-ViT-H-14 Face-Centric Vision Evaluator...")
        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        self.processor = CLIPImageProcessor.from_pretrained("laion/CLIP-ViT-H-14-laion2B-s32B-b79K")
        self.model = CLIPVisionModelWithProjection.from_pretrained(
            "laion/CLIP-ViT-H-14-laion2B-s32B-b79K",
            torch_dtype=torch.float16 if self.device == "cuda" else torch.float32
        ).to(self.device)
        self.model.eval()

    def crop_face_region(self, img: Image.Image, is_avatar_ref: bool = False) -> Image.Image:
        """Extract centered head & face region to isolate facial identity from background/dress."""
        w, h = img.size
        if is_avatar_ref:
            # Avatar is already a close-up/square face crop
            return img
        # For generated 512x768 medium shot, crop the head & face upper bounding box
        left = int(w * 0.12)
        top = int(h * 0.02)
        right = int(w * 0.88)
        bottom = int(h * 0.52)
        return img.crop((left, top, right, bottom))

    def get_face_embedding(self, img_path: str, is_avatar_ref: bool = False) -> np.ndarray:
        try:
            raw_img = Image.open(img_path).convert("RGB")
            face_img = self.crop_face_region(raw_img, is_avatar_ref=is_avatar_ref)
            inputs = self.processor(images=face_img, return_tensors="pt").to(self.device)
            with torch.no_grad():
                outputs = self.model(**inputs)
                emb = outputs.image_embeds.squeeze().cpu().numpy()
                norm = np.linalg.norm(emb)
                return emb / (norm + 1e-8)
        except Exception as ex:
            print(f"[ERROR] Failed to extract face embedding from {img_path}: {ex}")
            return np.zeros(1024)

    def compute_similarity(self, emb1: np.ndarray, emb2: np.ndarray) -> float:
        if np.all(emb1 == 0) or np.all(emb2 == 0):
            return 0.0
        return float(np.dot(emb1, emb2))

def run_face_identity_benchmark():
    print("=" * 86)
    print("PROJECT00: EMPIRICAL FACE-CENTRIC VISUAL IDENTITY GPU BENCHMARK")
    print(f"Target Server: {COMFY_URL}")
    print(f"Metric: CLIP-ViT-H-14 Face-Region Cosine Similarity")
    print("=" * 86)

    evaluator = FaceIdentityEvaluator()
    THRESHOLD = 0.75 # Calibrated Face Cosine Similarity Threshold

    # Configurations to benchmark: Baseline (0.55/0.75) vs Candidate (0.65/0.85)
    configs = [
        {"name": "Baseline (Weight=0.55, EndAt=0.75)", "weight": 0.55, "end_at": 0.75},
        {"name": "Candidate (Weight=0.65, EndAt=0.85)", "weight": 0.65, "end_at": 0.85}
    ]

    summary_records = []

    for cfg in configs:
        print(f"\n{'#' * 86}")
        print(f"RUNNING CONFIGURATION: {cfg['name']}")
        print(f"{'#' * 86}")

        cfg_all_scores = []

        for char in BENCHMARK_CHARACTERS:
            char_name = char["name"]
            ref_path = os.path.join(COMFY_INPUT_DIR, char["reference_image"])
            if not os.path.exists(ref_path):
                print(f"[WARN] Missing reference image at {ref_path}, skipping {char_name}")
                continue

            ref_emb = evaluator.get_face_embedding(ref_path, is_avatar_ref=True)
            print(f"\n--- Archetype: {char_name} ---")

            for scn in char["scenarios"]:
                scn_name = scn["name"]
                print(f"  > Scenario: {scn_name}")
                for seed in scn["seeds"]:
                    wf = build_workflow(char["reference_image"], scn["prompt"], scn["negative"], seed, cfg["weight"], cfg["end_at"])
                    q = queue_prompt(wf)
                    gen_path = wait_for_prompt_completion(q["prompt_id"])

                    gen_emb = evaluator.get_face_embedding(gen_path, is_avatar_ref=False)
                    sim = evaluator.compute_similarity(ref_emb, gen_emb)
                    cfg_all_scores.append(sim)

                    status = "PASS" if sim >= THRESHOLD else "BELOW_THRESHOLD"
                    print(f"    [Seed {seed}] File: {os.path.basename(gen_path)} | Face Sim: {sim:.4f} | Status: {status}")

        mean_sim = float(np.mean(cfg_all_scores))
        min_sim = float(np.min(cfg_all_scores))
        std_sim = float(np.std(cfg_all_scores))
        pass_count = sum(1 for s in cfg_all_scores if s >= THRESHOLD)
        pass_rate = (pass_count / len(cfg_all_scores)) * 100.0 if cfg_all_scores else 0.0

        summary_records.append({
            "config": cfg["name"],
            "mean": mean_sim,
            "min": min_sim,
            "std": std_sim,
            "pass_rate": pass_rate,
            "samples": len(cfg_all_scores)
        })

    print("\n" + "=" * 86)
    print("FINAL COMPARISON: BASELINE (0.55/0.75) vs CANDIDATE (0.65/0.85)")
    print("=" * 86)
    print(f"{'Configuration':<40} | {'Mean Sim':<10} | {'Min Sim':<10} | {'StdDev':<10} | {'Pass Rate':<10}")
    print("-" * 86)
    for r in summary_records:
        print(f"{r['config']:<40} | {r['mean']:<10.4f} | {r['min']:<10.4f} | {r['std']:<10.4f} | {r['pass_rate']:<9.1f}%")
    print("=" * 86)

if __name__ == "__main__":
    run_face_identity_benchmark()
