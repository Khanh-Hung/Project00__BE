import os
import sys
import json
import time
import shutil
import urllib.request
import urllib.parse
import traceback
import torch
import numpy as np

try:
    sys.stdout.reconfigure(line_buffering=True)
except Exception:
    pass
from PIL import Image, ImageDraw, ImageFont
from transformers import CLIPVisionModelWithProjection, CLIPImageProcessor, CLIPTokenizer, CLIPTextModelWithProjection

COMFY_URL = os.environ.get("COMFY_URL", "http://127.0.0.1:8188")
COMFY_INPUT_DIR = os.environ.get("COMFY_INPUT_DIR", r"D:\ComfyUI_windows_portable\ComfyUI\input")
COMFY_OUTPUT_DIR = os.environ.get("COMFY_OUTPUT_DIR", r"D:\ComfyUI_windows_portable\ComfyUI\output")
EVAL_ARTIFACTS_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "eval_artifacts_v23"))
AUTHORITATIVE_REQUESTS_JSON = os.path.join(EVAL_ARTIFACTS_DIR, "authoritative_compiled_requests.json")

CHARACTER_ATTRIBUTES = {
    "character_01_lyra": {
        "title": "Silver Dragon Saintess",
        "avatar_seed": 777111,
        "avatar_prompt": "masterpiece, best quality, solo, 1girl, female, feminine face, long silver white hair, striking crimson red eyes, sharp black dragon horns with glowing red accents on head, delicate porcelain skin, gentle expression, close up portrait, sharp focus",
        "avatar_negative": "2girls, multiple people, bad anatomy, bad hands, 1man, male, masculine face, deformed horns",
        "hair": {"pos": "long silver white hair", "neg": ["short black hair", "bright yellow blonde hair", "pink hair"]},
        "eyes": {"pos": "striking crimson red eyes", "neg": ["blue eyes", "green eyes", "brown eyes"]},
        "feature": {"pos": "sharp black dragon horns on head", "neg": ["no horns on head", "cat ears", "elf ears"]}
    },
    "character_02_elysia": {
        "title": "High Elf Scholar",
        "avatar_seed": 888222,
        "avatar_prompt": "masterpiece, best quality, solo, 1girl, female, feminine face, wavy pastel pink hair, crystal clear sapphire blue eyes, long elegant pointed elf ears, fair skin, cute warm smile, close up portrait, soft natural light, sharp focus",
        "avatar_negative": "2girls, multiple people, bad anatomy, bad hands, 1man, male, round human ears",
        "hair": {"pos": "wavy pastel pink hair", "neg": ["black hair", "blonde hair", "green hair"]},
        "eyes": {"pos": "crystal clear sapphire blue eyes", "neg": ["red eyes", "brown eyes", "dark black eyes"]},
        "feature": {"pos": "pointed elf ears", "neg": ["round human ears", "animal horns", "animal ears"]}
    },
    "character_03_valerius": {
        "title": "Shadow Knight Commander",
        "avatar_seed": 999333,
        "avatar_prompt": "masterpiece, best quality, solo, 1man, male, masculine face, short textured jet black hair, sharp piercing golden amber eyes, chiseled handsome jawline, dark steel knight commander armor with silver trims, close up portrait, dramatic lighting, sharp focus",
        "avatar_negative": "2girls, multiple people, bad anatomy, bad hands, 1girl, anime girl, female, woman, breasts, feminine face",
        "hair": {"pos": "short textured jet black hair", "neg": ["long blonde hair", "pink hair", "silver hair"]},
        "eyes": {"pos": "sharp piercing golden amber eyes", "neg": ["blue eyes", "crimson red eyes", "green eyes"]},
        "feature": {"pos": "dark steel knight commander armor", "neg": ["casual dress", "swimsuit", "t-shirt"]}
    }
}

class ComprehensiveEvaluator:
    def __init__(self):
        print("Loading CLIP Vision & Text Models for Multimodal Production Evaluation...")
        clip_model_id = "openai/clip-vit-large-patch14"
        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        self.processor = CLIPImageProcessor.from_pretrained(clip_model_id)
        self.vision_model = CLIPVisionModelWithProjection.from_pretrained(clip_model_id).to(self.device).eval()
        self.tokenizer = CLIPTokenizer.from_pretrained(clip_model_id)
        self.text_model = CLIPTextModelWithProjection.from_pretrained(clip_model_id).to(self.device).eval()

    def get_full_image_embedding(self, image_path):
        image = Image.open(image_path).convert("RGB")
        inputs = self.processor(images=image, return_tensors="pt").to(self.device)
        with torch.no_grad():
            outputs = self.vision_model(**inputs)
            emb = outputs.image_embeds
            emb = emb / emb.norm(p=2, dim=-1, keepdim=True)
        return emb.cpu().numpy()[0]

    def locate_and_crop_face_region(self, image):
        w, h = image.size
        crop_box = (int(w * 0.20), int(h * 0.05), int(w * 0.80), int(h * 0.50))
        return image.crop(crop_box)

    def get_face_embedding(self, image_path):
        image = Image.open(image_path).convert("RGB")
        face_crop = self.locate_and_crop_face_region(image)
        inputs = self.processor(images=face_crop, return_tensors="pt").to(self.device)
        with torch.no_grad():
            outputs = self.vision_model(**inputs)
            emb = outputs.image_embeds
            emb = emb / emb.norm(p=2, dim=-1, keepdim=True)
        return emb.cpu().numpy()[0]

    def get_text_embedding(self, text):
        inputs = self.tokenizer([text], padding=True, return_tensors="pt").to(self.device)
        with torch.no_grad():
            outputs = self.text_model(**inputs)
            emb = outputs.text_embeds
            emb = emb / emb.norm(p=2, dim=-1, keepdim=True)
        return emb.cpu().numpy()[0]

    def compute_similarity(self, emb1, emb2):
        return float(np.dot(emb1, emb2) / (np.linalg.norm(emb1) * np.linalg.norm(emb2)))

    def evaluate_action_compliance(self, image_path, target_action_prompt, negative_action_prompts):
        img_emb = self.get_full_image_embedding(image_path)
        pos_emb = self.get_text_embedding(target_action_prompt)
        pos_sim = self.compute_similarity(img_emb, pos_emb)
        neg_sims = [self.compute_similarity(img_emb, self.get_text_embedding(n)) for n in negative_action_prompts]
        max_neg_sim = max(neg_sims) if neg_sims else 0.0
        margin = pos_sim - max_neg_sim
        return {
            "pos_sim": float(pos_sim),
            "max_neg_sim": float(max_neg_sim),
            "margin": float(margin),
            "is_compliant": bool(margin > 0.0)
        }

    def evaluate_attribute(self, image_path, attr_dict):
        img_emb = self.get_full_image_embedding(image_path)
        pos_emb = self.get_text_embedding(attr_dict["pos"])
        pos_sim = self.compute_similarity(img_emb, pos_emb)
        neg_sims = [self.compute_similarity(img_emb, self.get_text_embedding(n)) for n in attr_dict["neg"]]
        max_neg_sim = max(neg_sims) if neg_sims else 0.0
        is_retained = bool(pos_sim > max_neg_sim)
        return is_retained, float(pos_sim - max_neg_sim)

def queue_prompt(prompt_workflow):
    data = json.dumps({"prompt": prompt_workflow}).encode('utf-8')
    req = urllib.request.Request(f"{COMFY_URL}/prompt", data=data, headers={'Content-Type': 'application/json'})
    try:
        with urllib.request.urlopen(req) as resp:
            return json.loads(resp.read())
    except urllib.error.HTTPError as e:
        err_msg = e.read().decode('utf-8')
        print(f"[COMFY ERROR 400] {err_msg}")
        raise

def wait_for_prompt_completion(prompt_id, timeout=120):
    start = time.time()
    while time.time() - start < timeout:
        req = urllib.request.Request(f"{COMFY_URL}/history/{prompt_id}")
        with urllib.request.urlopen(req) as resp:
            history = json.loads(resp.read())
            if prompt_id in history:
                outputs = history[prompt_id].get("outputs", {})
                for node_id, node_out in outputs.items():
                    if "images" in node_out:
                        img_info = node_out["images"][0]
                        filename = img_info["filename"]
                        subfolder = img_info.get("subfolder", "")
                        return os.path.join(COMFY_OUTPUT_DIR, subfolder, filename)
        time.sleep(1.0)
    raise TimeoutError(f"Prompt {prompt_id} did not finish within {timeout}s.")

def load_v2_workflow_template():
    workflow_path = os.path.abspath(os.path.join(os.path.dirname(__file__), "production_workflow_v2_template.json"))
    with open(workflow_path, "r", encoding="utf-8") as f:
        return json.load(f)

def build_workflow(template, avatar_filename, prev_scene_filename, prompt_text, negative_text, seed, weight=0.12, end_at=0.25, weight_type="style transfer", slot2_active=True):
    wf = json.loads(json.dumps(template))
    wf["6"]["inputs"]["text"] = prompt_text
    wf["7"]["inputs"]["text"] = negative_text
    wf["3"]["inputs"]["seed"] = seed
    wf["1"]["inputs"]["image"] = avatar_filename

    if slot2_active and prev_scene_filename:
        wf["13"]["inputs"]["image"] = prev_scene_filename
        wf["14"]["inputs"]["weight"] = float(weight)
        wf["14"]["inputs"]["end_at"] = float(end_at)
        wf["14"]["inputs"]["weight_type"] = weight_type
        wf["3"]["inputs"]["model"] = ["14", 0]
    else:
        wf["3"]["inputs"]["model"] = ["10", 0]

    return wf

def ensure_canonical_avatar(char_id, char_name, meta, evaluator):
    avatar_filename = f"{char_name}_tight_face.png"
    avatar_path = os.path.join(COMFY_INPUT_DIR, avatar_filename)
    if os.path.exists(avatar_path):
        return avatar_path

    print(f"[{char_name}] Generating master canonical avatar with seed {meta['avatar_seed']}...")
    template = load_v2_workflow_template()
    wf = build_workflow(
        template=template,
        avatar_filename="dummy.png",
        prev_scene_filename=None,
        prompt_text=meta["avatar_prompt"],
        negative_text=meta["avatar_negative"],
        seed=meta["avatar_seed"],
        slot2_active=False
    )
    q = queue_prompt(wf)
    gen_path = wait_for_prompt_completion(q["prompt_id"])

    raw_img = Image.open(gen_path).convert("RGB")
    face_crop = evaluator.locate_and_crop_face_region(raw_img)
    face_crop.save(avatar_path)
    print(f"[{char_name}] Canonical cropped avatar saved: {avatar_path}")
    return avatar_path

def create_contact_sheet(char_dir, char_name, images_dict, out_path):
    W, H = 512, 768
    cols, rows = 3, 3
    sheet = Image.new("RGB", (W * cols, H * rows), color=(20, 20, 25))
    draw = ImageDraw.Draw(sheet)

    try:
        font = ImageFont.truetype("arial.ttf", 26)
    except Exception:
        font = ImageFont.load_default()

    keys = ["canonical", 1, 2, 3, 4, 5, 6, 7, 8]
    for idx, key in enumerate(keys):
        c = idx % cols
        r = idx // cols
        x_off, y_off = c * W, r * H

        if key in images_dict and os.path.exists(images_dict[key]):
            img = Image.open(images_dict[key]).resize((W, H), Image.Resampling.LANCZOS)
            sheet.paste(img, (x_off, y_off))
            label = "CANONICAL AVATAR (SLOT 1)" if key == "canonical" else f"TURN {key:02d}"
            draw.rectangle([x_off, y_off, x_off + W, y_off + 40], fill=(0, 0, 0, 180))
            draw.text((x_off + 15, y_off + 8), label, fill=(255, 220, 100) if key == "canonical" else (255, 255, 255), font=font)

    sheet.save(out_path)
    print(f"[{char_name}] Contact Sheet (3x3) saved: {out_path}")

def evaluate_character_from_requests(char_id, requests, template, evaluator):
    char_name = requests[0]["CharacterName"]
    gender_str = requests[0]["Gender"]
    meta = CHARACTER_ATTRIBUTES[char_id]

    print(f"\n==========================================================================================")
    print(f"EVALUATING PRODUCTION CHARACTER: {char_name.upper()} ({meta['title']}) [Gender: {gender_str}]")
    print(f"Using Authoritative C#-Compiled Prompts, Negatives, and Dynamic Slot 2 Parameters")
    print(f"==========================================================================================")

    char_dir = os.path.join(EVAL_ARTIFACTS_DIR, char_id)
    os.makedirs(char_dir, exist_ok=True)

    avatar_path = ensure_canonical_avatar(char_id, char_name, meta, evaluator)
    canonical_artifact_path = os.path.join(char_dir, "canonical_avatar.png")
    shutil.copyfile(avatar_path, canonical_artifact_path)
    avatar_emb = evaluator.get_face_embedding(avatar_path)

    # Setup gender evaluation attributes based on canonical gender
    if gender_str.lower() == "male":
        gender_attr = {"pos": "1man, anime man, male, boy, masculine face", "neg": ["1girl, anime girl, female, woman, breasts, feminine face"]}
    else:
        gender_attr = {"pos": "1girl, anime girl, female, woman, feminine face", "neg": ["1man, anime man, male, boy, masculine face, facial hair"]}

    results = []
    prev_scene_filename = None
    prev_full_emb = None
    images_dict = {"canonical": canonical_artifact_path}

    for req in requests:
        turn = req["Turn"]
        location = req["Location"]
        action = req["Action"]
        is_transition = req["IsTransition"]
        seed = req["Seed"]
        prompt = req["CompiledPrompt"]
        negative = req["CompiledNegative"]
        weight = req["Slot2Weight"]
        end_at = req["Slot2EndAt"]
        slot2_active = req["Slot2Active"]
        target_action_prompt = req["TargetActionPrompt"]
        neg_action_prompts = req["NegativeActionPrompts"]

        avatar_input_filename = f"{char_name}_tight_face.png"

        wf = build_workflow(
            template=template,
            avatar_filename=avatar_input_filename,
            prev_scene_filename=prev_scene_filename,
            prompt_text=prompt,
            negative_text=negative,
            seed=seed,
            weight=weight,
            end_at=end_at,
            weight_type=req.get("WeightType", "style transfer"),
            slot2_active=slot2_active
        )
        q = queue_prompt(wf)
        gen_path = wait_for_prompt_completion(q["prompt_id"])

        artifact_filename = f"turn_{turn:02d}_{action.replace('/', '_')}.png"
        artifact_path = os.path.join(char_dir, artifact_filename)
        shutil.copyfile(gen_path, artifact_path)
        images_dict[turn] = artifact_path

        next_input_filename = f"{char_id}_turn_{turn}_input.png"
        next_input_path = os.path.join(COMFY_INPUT_DIR, next_input_filename)
        shutil.copyfile(gen_path, next_input_path)

        # 1. CLIPIdentitySim
        face_emb = evaluator.get_face_embedding(gen_path)
        clip_identity_sim = evaluator.compute_similarity(avatar_emb, face_emb)

        # 2. Scene Continuity
        full_emb = evaluator.get_full_image_embedding(gen_path)
        scene_sim = evaluator.compute_similarity(prev_full_emb, full_emb) if prev_full_emb is not None else 1.0

        # 3. Action Discrimination
        action_metrics = evaluator.evaluate_action_compliance(gen_path, target_action_prompt, neg_action_prompts)

        # 4. Attribute Retention
        gender_ok, _ = evaluator.evaluate_attribute(gen_path, gender_attr)
        hair_ok, _ = evaluator.evaluate_attribute(gen_path, meta["hair"])
        eyes_ok, _ = evaluator.evaluate_attribute(gen_path, meta["eyes"])
        feat_ok, _ = evaluator.evaluate_attribute(gen_path, meta["feature"])

        trans_tag = "[TRANSITION]" if is_transition else "[SAME-SCENE]"
        comp_tag = "PASS" if action_metrics["is_compliant"] else "WARN"
        print(f"[{char_name} | Turn {turn}/8] {location:<32} {trans_tag:<12} | Identity: {clip_identity_sim:.4f} | Scene: {scene_sim:.4f} | Margin: {action_metrics['margin']:+.4f} [{comp_tag}] | Gender:{'✓' if gender_ok else '✗'} Hair:{'✓' if hair_ok else '✗'} Eye:{'✓' if eyes_ok else '✗'} Feat:{'✓' if feat_ok else '✗'}")

        results.append({
            "turn": turn,
            "location": location,
            "action": action,
            "is_transition": is_transition,
            "clip_identity_sim": clip_identity_sim,
            "scene_sim": scene_sim,
            "pos_sim": action_metrics["pos_sim"],
            "max_neg_sim": action_metrics["max_neg_sim"],
            "action_margin": action_metrics["margin"],
            "is_action_compliant": action_metrics["is_compliant"],
            "gender_retained": gender_ok,
            "hair_retained": hair_ok,
            "eyes_retained": eyes_ok,
            "feature_retained": feat_ok,
            "artifact_path": artifact_path
        })

        prev_scene_filename = next_input_filename
        prev_full_emb = full_emb

    # Contact Sheet
    contact_sheet_path = os.path.join(char_dir, f"{char_id}_contact_sheet.png")
    create_contact_sheet(char_dir, char_name, images_dict, contact_sheet_path)

    face_scores = [r["clip_identity_sim"] for r in results]
    same_scene_scores = [r["scene_sim"] for r in results[1:] if not r["is_transition"]]
    trans_scene_scores = [r["scene_sim"] for r in results[1:] if r["is_transition"]]
    action_margins = [r["action_margin"] for r in results]
    action_pass_count = sum(1 for r in results if r["is_action_compliant"])
    gender_count = sum(1 for r in results if r["gender_retained"])
    hair_count = sum(1 for r in results if r["hair_retained"])
    eye_count = sum(1 for r in results if r["eyes_retained"])
    feat_count = sum(1 for r in results if r["feature_retained"])
    slope = float(np.polyfit(np.arange(1, len(results) + 1), face_scores, 1)[0])

    summary = {
        "character": char_name,
        "title": meta["title"],
        "gender": gender_str,
        "mean_identity_sim": float(np.mean(face_scores)),
        "min_identity_sim": float(np.min(face_scores)),
        "max_identity_sim": float(np.max(face_scores)),
        "identity_slope": slope,
        "mean_same_scene_continuity": float(np.mean(same_scene_scores)) if same_scene_scores else 1.0,
        "mean_transition_continuity": float(np.mean(trans_scene_scores)) if trans_scene_scores else 1.0,
        "action_compliance_rate": f"{action_pass_count}/{len(results)} ({action_pass_count/len(results)*100:.1f}%)",
        "mean_action_margin": float(np.mean(action_margins)),
        "gender_retention": f"{gender_count}/{len(results)}",
        "hair_retention": f"{hair_count}/{len(results)}",
        "eye_retention": f"{eye_count}/{len(results)}",
        "feature_retention": f"{feat_count}/{len(results)}",
        "contact_sheet": contact_sheet_path,
        "turns": results
    }

    return summary

def main():
    print("=" * 110)
    print("PROJECT00: AUTHORITATIVE C#-COMPILED BACKEND GPU BENCHMARK (PR #22)")
    print(f"Loading C# backend compiled requests from: {AUTHORITATIVE_REQUESTS_JSON}")
    print("=" * 110)

    if not os.path.exists(AUTHORITATIVE_REQUESTS_JSON):
        raise FileNotFoundError(f"Missing {AUTHORITATIVE_REQUESTS_JSON}. Run 'dotnet test --filter ProductionBenchmarkCompilerExporter' first.")

    with open(AUTHORITATIVE_REQUESTS_JSON, "r", encoding="utf-8") as f:
        all_requests = json.load(f)

    grouped_requests = {}
    for req in all_requests:
        cid = req["CharacterId"]
        if cid not in grouped_requests:
            grouped_requests[cid] = []
        grouped_requests[cid].append(req)

    template = load_v2_workflow_template()
    evaluator = ComprehensiveEvaluator()

    all_summaries = []
    for char_id, reqs in grouped_requests.items():
        summary = evaluate_character_from_requests(char_id, reqs, template, evaluator)
        all_summaries.append(summary)

    summary_json_path = os.path.join(EVAL_ARTIFACTS_DIR, "production_evaluation_summary.json")
    with open(summary_json_path, "w", encoding="utf-8") as f:
        json.dump(all_summaries, f, indent=2)

    print("\n" + "=" * 120)
    print("AGGREGATED PRODUCTION EVALUATION SUMMARY (CONSUMING C# BACKEND PROMPT PIPELINE)")
    print("=" * 120)
    print(f"{'Character Persona':<25} | {'Mean Identity':<14} | {'Degradation Slope':<18} | {'Same-Scene Sim':<15} | {'Trans Scene Sim':<16} | {'Action Margin':<14} | {'Gender':<8} | {'Hair/Eye/Feat'}")
    print("-" * 120)
    for s in all_summaries:
        print(f"{s['character'] + ' (' + s['title'] + ')':<25} | {s['mean_identity_sim']:<14.4f} | {s['identity_slope']:<+18.5f} | {s['mean_same_scene_continuity']:<15.4f} | {s['mean_transition_continuity']:<16.4f} | {s['mean_action_margin']:<+14.4f} | {s['gender_retention']:<8} | {s['hair_retention']}/{s['eye_retention']}/{s['feature_retention']}")
    print("=" * 120)
    print(f"Artifacts, full frame PNGs, and contact sheets saved to: {EVAL_ARTIFACTS_DIR}")

if __name__ == '__main__':
    try:
        main()
    except Exception as e:
        traceback.print_exc()
        sys.exit(1)
