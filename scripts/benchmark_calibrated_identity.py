import os
import sys
import json
import time
import urllib.request
import torch
import numpy as np
from PIL import Image
import torchvision.transforms as transforms
import torchvision.models as models

COMFY_URL = "http://127.0.0.1:8188"
COMFY_INPUT_DIR = r"D:\ComfyUI_windows_portable\ComfyUI\input"
COMFY_OUTPUT_DIR = r"D:\ComfyUI_windows_portable\ComfyUI\output"

# Calibrated Benchmark with Lyra's TRUE Canonical Traits (Silver hair, striking red eyes, black and red horns)
CALIBRATED_BENCHMARKS = [
    {
        "name": "Lyra (Canonical Horns & Red Eyes - Sanctuary)",
        "reference_image": "Lyra_tight_face.png",
        "prompt": "masterpiece, best quality, solo, 1girl, long white hair, striking red eyes, black horns with red accents on head, delicate elegant face, sharp jawline, porcelain skin, wearing white and gold dress, standing in grand sanctuary hall, soft daylight, medium shot, slight 3/4 turn",
        "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, extra digits, cropped, blurry, low quality",
        "seeds": [700007, 800008, 900009],
        "ip_weight": 0.65,
        "ip_end_at": 0.85
    }
]

def build_visual_identity_workflow(reference_img_name, prompt, negative, seed, ip_weight=0.65, ip_end_at=0.85):
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
            "inputs": { "filename_prefix": f"Calibrated_Identity_{seed}", "images": ["9", 0] }
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

class FeatureExtractor:
    def __init__(self):
        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        weights = models.ResNet50_Weights.DEFAULT
        self.model = models.resnet50(weights=weights).to(self.device)
        self.model.eval()
        self.feature_layer = torch.nn.Sequential(*list(self.model.children())[:-1])
        self.preprocess = weights.transforms()

    def get_embedding(self, img_path):
        img = Image.open(img_path).convert("RGB")
        tensor = self.preprocess(img).unsqueeze(0).to(self.device)
        with torch.no_grad():
            feat = self.feature_layer(tensor).squeeze().cpu().numpy()
            norm = np.linalg.norm(feat)
            return feat / (norm + 1e-8)

    def cosine_similarity(self, emb1, emb2):
        return float(np.dot(emb1, emb2))

def run_calibrated_benchmark():
    print("=" * 78)
    print("PROJECT00: CALIBRATED VISUAL IDENTITY GPU BENCHMARK (PHASE 20.1)")
    print("=" * 78)

    extractor = FeatureExtractor()
    IDENTITY_SIMILARITY_THRESHOLD = 0.75
    results = []

    for char_idx, char in enumerate(CALIBRATED_BENCHMARKS):
        char_name = char["name"]
        ref_filename = char["reference_image"]
        ref_path = os.path.join(COMFY_INPUT_DIR, ref_filename)
        ref_emb = extractor.get_embedding(ref_path)

        print(f"\n--- Testing: {char_name} (Weight={char['ip_weight']}, EndAt={char['ip_end_at']}) ---")
        char_scores = []
        for seed in char["seeds"]:
            print(f"  > Generating Image for Seed={seed}...")
            workflow = build_visual_identity_workflow(ref_filename, char["prompt"], char["negative"], seed, char["ip_weight"], char["ip_end_at"])
            queue_res = queue_prompt(workflow)
            prompt_id = queue_res["prompt_id"]

            generated_path = wait_for_prompt_completion(prompt_id)
            gen_emb = extractor.get_embedding(generated_path)
            score = extractor.cosine_similarity(ref_emb, gen_emb)
            char_scores.append(score)
            status = "PASS" if score >= IDENTITY_SIMILARITY_THRESHOLD else "BELOW_THRESHOLD"
            print(f"    [Completed] Image: {os.path.basename(generated_path)} | Identity Similarity: {score:.4f} | Status: {status}")

        mean_score = float(np.mean(char_scores))
        min_score = float(np.min(char_scores))
        results.append({
            "character": char_name,
            "mean_similarity": mean_score,
            "min_similarity": min_score,
            "pass": min_score >= IDENTITY_SIMILARITY_THRESHOLD
        })

    print("\n" + "=" * 78)
    print("CALIBRATED BENCHMARK RESULTS")
    print("=" * 78)
    for r in results:
        print(f"{r['character']:<45} | Mean: {r['mean_similarity']:.4f} | Min: {r['min_similarity']:.4f} | Status: {'PASS' if r['pass'] else 'WARN'}")
    print("=" * 78)

if __name__ == "__main__":
    run_calibrated_benchmark()
