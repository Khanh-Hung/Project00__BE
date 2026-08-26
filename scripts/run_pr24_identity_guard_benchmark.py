"""
PR #24 Stage 1 & Stage 2 Identity Quality Guard Benchmark Runner
Evaluates 2 modes:
  Mode 1: Baseline PR23 (Unguarded, 1-shot)
  Mode 2: PR24 Identity Quality Guard (Quality Evaluation + Deterministic Mitigation Loop + Quarantine)

Stage 1: 3 personas x 4 turns x 2 modes = 24 GPU frames
Stage 2: 3 personas x 8 turns x 2 modes = 48 GPU frames
"""

import os
import sys

try:
    sys.stdout.reconfigure(line_buffering=True)
except Exception:
    pass

import json
import time
import shutil
import urllib.request
import urllib.parse
import numpy as np
from PIL import Image, ImageDraw, ImageFont
import torch
from transformers import CLIPImageProcessor, CLIPVisionModelWithProjection, CLIPTokenizer, CLIPTextModelWithProjection

COMFY_HOST = "127.0.0.1:8188"
COMFY_OUTPUT_DIR = r"D:\ComfyUI_windows_portable\ComfyUI\output"
INPUT_DIR = r"D:\ComfyUI_windows_portable\ComfyUI\input"
BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REQUESTS_FILE = os.path.join(BASE_DIR, "eval_artifacts_v23", "authoritative_compiled_requests.json")
TEMPLATE_PATH = os.path.join(BASE_DIR, "scripts", "production_workflow_v2_template.json")
ARTIFACTS_DIR = os.path.join(BASE_DIR, "eval_artifacts_pr24")
os.makedirs(ARTIFACTS_DIR, exist_ok=True)

# -------------------------------------------------------------
# CLIP Evaluator Initialization
# -------------------------------------------------------------
clip_model_id = "openai/clip-vit-large-patch14"
device = "cuda" if torch.cuda.is_available() else "cpu"
print(f"Loading CLIP Evaluator ({clip_model_id}) on {device}...", flush=True)
clip_processor = CLIPImageProcessor.from_pretrained(clip_model_id)
clip_vision_model = CLIPVisionModelWithProjection.from_pretrained(clip_model_id).to(device).eval()
clip_tokenizer = CLIPTokenizer.from_pretrained(clip_model_id)
clip_text_model = CLIPTextModelWithProjection.from_pretrained(clip_model_id).to(device).eval()
print(f"CLIP Evaluator ready on {device}!", flush=True)

def get_image_embedding(image_path: str):
    img = Image.open(image_path).convert("RGB")
    inputs = clip_processor(images=img, return_tensors="pt").to(device)
    with torch.no_grad():
        outputs = clip_vision_model(**inputs)
        emb = outputs.image_embeds[0].cpu().numpy()
        emb = emb / (np.linalg.norm(emb) + 1e-8)
    return emb

def get_text_embedding(text: str):
    inputs = clip_tokenizer(text=[text], return_tensors="pt", padding=True, truncation=True).to(device)
    with torch.no_grad():
        outputs = clip_text_model(**inputs)
        emb = outputs.text_embeds[0].cpu().numpy()
        emb = emb / (np.linalg.norm(emb) + 1e-8)
    return emb

def cosine_similarity(a: np.ndarray, b: np.ndarray) -> float:
    return float(np.dot(a, b) / (np.linalg.norm(a) * np.linalg.norm(b) + 1e-8))

# -------------------------------------------------------------
# Deterministic Seed Derivation (Matches C# SplitMix64)
# -------------------------------------------------------------
def derive_seed(base_seed: int, attempt: int) -> int:
    if attempt <= 1:
        return int(base_seed)
    z = (base_seed + (attempt * 0x9E3779B97F4A7C15)) & 0xFFFFFFFFFFFFFFFF
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9 & 0xFFFFFFFFFFFFFFFF
    z = (z ^ (z >> 27)) * 0x94D049BB133111EB & 0xFFFFFFFFFFFFFFFF
    res = (z ^ (z >> 31)) & 0x7FFFFFFFFFFFFFFF
    return int(res)

# -------------------------------------------------------------
# ComfyUI Dispatcher
# -------------------------------------------------------------
def queue_prompt(prompt_workflow):
    data = json.dumps({"prompt": prompt_workflow}).encode("utf-8")
    req = urllib.request.Request(f"http://{COMFY_HOST}/prompt", data=data, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req) as resp:
        return json.loads(resp.read().decode("utf-8"))

def wait_for_execution(prompt_id, timeout=120):
    start = time.time()
    while time.time() - start < timeout:
        try:
            with urllib.request.urlopen(f"http://{COMFY_HOST}/history/{prompt_id}") as resp:
                history = json.loads(resp.read().decode("utf-8"))
                if prompt_id in history:
                    outputs = history[prompt_id].get("outputs", {})
                    for nid, nout in outputs.items():
                        if "images" in nout and len(nout["images"]) > 0:
                            img_info = nout["images"][0]
                            fn = img_info["filename"]
                            sub = img_info.get("subfolder", "")
                            if sub:
                                return os.path.join(sub, fn)
                            return fn
        except Exception:
            pass
        time.sleep(1.0)
    raise TimeoutError(f"Generation timed out after {timeout}s for prompt_id {prompt_id}")

def build_workflow_graph(template, prompt_text, negative_text, seed, avatar_file, prev_scene_file=None, slot2_weight=0.12, slot2_end_at=0.25, weight_type="style transfer"):
    import copy
    wf = copy.deepcopy(template)
    wf["6"]["inputs"]["text"] = prompt_text
    wf["7"]["inputs"]["text"] = negative_text
    wf["3"]["inputs"]["seed"] = int(seed)
    wf["1"]["inputs"]["image"] = os.path.basename(avatar_file)

    if prev_scene_file and os.path.exists(prev_scene_file) and slot2_weight > 0.0:
        wf["13"]["inputs"]["image"] = os.path.basename(prev_scene_file)
        wf["14"]["inputs"]["weight"] = round(float(slot2_weight), 4)
        wf["14"]["inputs"]["end_at"] = round(float(slot2_end_at), 4)
        wf["14"]["inputs"]["weight_type"] = weight_type
        wf["3"]["inputs"]["model"] = ["14", 0]
    else:
        # Full bypass: remove Node 13 & 14, connect KSampler directly to Node 10
        wf.pop("13", None)
        wf.pop("14", None)
        wf["3"]["inputs"]["model"] = ["10", 0]

    return wf

# -------------------------------------------------------------
# Contact Sheet Generator
# -------------------------------------------------------------
def create_contact_sheet(frame_paths, frame_labels, out_path, avatar_path):
    cols = len(frame_paths) + 1
    w, h = 300, 450
    margin = 10
    sheet_w = cols * w + (cols + 1) * margin
    sheet_h = h + 2 * margin + 40
    sheet = Image.new("RGB", (sheet_w, sheet_h), (25, 25, 30))
    draw = ImageDraw.Draw(sheet)

    # Place avatar
    if os.path.exists(avatar_path):
        av_img = Image.open(avatar_path).convert("RGB").resize((w, h))
        sheet.paste(av_img, (margin, margin))
        draw.text((margin + 10, h + margin + 10), "Canonical Avatar", fill=(255, 215, 0))

    # Place frames
    for i, (fp, lbl) in enumerate(zip(frame_paths, frame_labels)):
        if os.path.exists(fp):
            f_img = Image.open(fp).convert("RGB").resize((w, h))
            x = (i + 1) * w + (i + 2) * margin
            sheet.paste(f_img, (x, margin))
            draw.text((x + 10, h + margin + 10), lbl, fill=(240, 240, 240))

    sheet.save(out_path)

# -------------------------------------------------------------
# Identity Quality Evaluation Contract
# -------------------------------------------------------------
def evaluate_frame_quality(img_path, avatar_path, char_name, turn_data):
    img_emb = get_image_embedding(img_path)
    avatar_emb = get_image_embedding(avatar_path)
    face_sim = float(cosine_similarity(img_emb, avatar_emb))

    inv_violated = False
    if "Valerius" in char_name:
        g_male = cosine_similarity(img_emb, get_text_embedding("1man, handsome male knight, masculine face"))
        g_female = cosine_similarity(img_emb, get_text_embedding("1girl, anime girl, female, woman, breasts"))
        if g_female > g_male + 0.05:
            inv_violated = True

    feat_pass = True
    feat_score = 1.0
    if "Valerius" in char_name:
        f_pos = cosine_similarity(img_emb, get_text_embedding("black obsidian knight armor, silver filigree pauldron crest"))
        f_neg = cosine_similarity(img_emb, get_text_embedding("cloth tunic, casual modern shirt, everyday clothes"))
        feat_pass = (f_pos > f_neg)
        feat_score = float(max(0.0, min(1.0, (f_pos - f_neg + 0.1) * 5.0)))
    elif "Elysia" in char_name:
        f_pos = cosine_similarity(img_emb, get_text_embedding("pointed elf ears, delicate crystalline elf ear tips"))
        f_neg = cosine_similarity(img_emb, get_text_embedding("human round ears, normal human ears"))
        feat_pass = (f_pos > f_neg)
        feat_score = float(max(0.0, min(1.0, (f_pos - f_neg + 0.1) * 5.0)))
    elif "Lyra" in char_name:
        f_pos = cosine_similarity(img_emb, get_text_embedding("obsidian dragon horns, glowing crimson horn markings"))
        f_neg = cosine_similarity(img_emb, get_text_embedding("human head without horns, normal human head"))
        feat_pass = (f_pos > f_neg)
        feat_score = float(max(0.0, min(1.0, (f_pos - f_neg + 0.1) * 5.0)))

    overall = 0.6 * face_sim + 0.4 * feat_score
    if inv_violated:
        status = "Failed"
    elif face_sim < 0.70 or not feat_pass:
        status = "Degraded"
    else:
        status = "Passed"

    return {
        "status": status,
        "face_similarity": float(face_sim),
        "feature_score": float(feat_score),
        "feature_passed": bool(feat_pass),
        "invariant_violated": bool(inv_violated),
        "overall_score": float(overall)
    }

# -------------------------------------------------------------
# Main Benchmark Execution
# -------------------------------------------------------------
def run_benchmark(max_turns=4, mode_filter=None):
    with open(REQUESTS_FILE, "r", encoding="utf-8") as f:
        all_requests = json.load(f)
    with open(TEMPLATE_PATH, "r", encoding="utf-8") as f:
        template = json.load(f)

    # Group flat requests list by CharacterId
    characters_data = {}
    for r in all_requests:
        cid = r["CharacterId"]
        if cid not in characters_data:
            characters_data[cid] = {
                "name": r["CharacterName"],
                "canonical_avatar_url": r["IdentityReferenceUrl"],
                "gender": r.get("Gender", "Female"),
                "turns": []
            }
        characters_data[cid]["turns"].append({
            "scene_revision": r["Turn"],
            "seed": int(r["Seed"]),
            "compiled_prompt": r["CompiledPrompt"],
            "compiled_negative_prompt": r["CompiledNegative"],
            "context": "ColdStart" if r["Turn"] == 1 else ("SceneTransition" if r.get("IsTransition") else "SameScene"),
            "target_action": r.get("TargetActionPrompt", ""),
            "negative_actions": r.get("NegativeActionPrompts", [])
        })

    modes = [
        {"id": "mode_1_baseline", "name": "Mode 1: Baseline PR23 (Unguarded 1-shot)", "guard": False},
        {"id": "mode_2_quality_guard", "name": "Mode 2: PR24 Identity Quality Guard (Auto-Mitigation + Quarantine)", "guard": True}
    ]

    if mode_filter:
        modes = [m for m in modes if m["id"] == mode_filter]

    results = []

    print(f"\n==========================================================================", flush=True)
    print(f"🚀 RUNNING PR #24 BENCHMARK: Max Turns={max_turns}, Total Modes={len(modes)}", flush=True)
    print(f"==========================================================================", flush=True)

    for mode in modes:
        mode_id = mode["id"]
        mode_name = mode["name"]
        use_guard = mode["guard"]
        mode_dir = os.path.join(ARTIFACTS_DIR, mode_id)
        os.makedirs(mode_dir, exist_ok=True)

        mode_summary = {"mode_id": mode_id, "mode_name": mode_name, "characters": {}}

        for char_key, char_info in characters_data.items():
            char_name = char_info["name"]
            avatar_url = char_info["canonical_avatar_url"]
            avatar_local = os.path.join(INPUT_DIR, os.path.basename(avatar_url))
            turns = char_info["turns"][:max_turns]

            print(f"\n--- [{mode_name}] Character: {char_name} ({len(turns)} turns) ---", flush=True)

            char_dir = os.path.join(mode_dir, char_key)
            os.makedirs(char_dir, exist_ok=True)

            last_known_good_image = None
            prev_frame_emb = None
            turn_results = []
            frame_paths = []
            frame_labels = []
            action_margins = []
            same_scene_sims = []
            trans_scene_sims = []

            for turn in turns:
                rev = turn["scene_revision"]
                base_seed = turn["seed"]
                prompt = turn["compiled_prompt"]
                neg = turn["compiled_negative_prompt"]
                context = turn["context"]
                is_cold = (context == "ColdStart") or (rev == 1)
                is_trans = (context == "SceneTransition")

                print(f"  Turn {rev} ({context}): BaseSeed={base_seed}", flush=True)

                attempt = 1
                max_attempts = 3 if use_guard else 1
                final_img_path = None
                final_eval = None
                final_seed = base_seed

                while attempt <= max_attempts:
                    cur_seed = derive_seed(base_seed, attempt)
                    if attempt == 1:
                        slot2_w = 0.12 if not is_cold else 0.0
                        slot2_e = 0.25 if not is_cold else 0.0
                    elif attempt == 2:
                        slot2_w = 0.06
                        slot2_e = 0.15
                    else:
                        slot2_w = 0.0
                        slot2_e = 0.0

                    prev_img_input = last_known_good_image if (not is_cold and last_known_good_image) else None

                    dest_name = f"{char_key}_turn_{rev:02d}_att_{attempt}.png"
                    dest_path = os.path.join(char_dir, dest_name)

                    if not os.path.exists(dest_path):
                        wf = build_workflow_graph(
                            template=template,
                            prompt_text=prompt,
                            negative_text=neg,
                            seed=cur_seed,
                            avatar_file=avatar_local,
                            prev_scene_file=prev_img_input,
                            slot2_weight=slot2_w,
                            slot2_end_at=slot2_e,
                            weight_type="style transfer"
                        )
                        q_res = queue_prompt(wf)
                        pid = q_res["prompt_id"]
                        fn = wait_for_execution(pid)
                        src_path = os.path.join(COMFY_OUTPUT_DIR, fn)
                        shutil.copyfile(src_path, dest_path)

                    eval_res = evaluate_frame_quality(dest_path, avatar_local, char_name, turn)
                    final_eval = eval_res
                    final_img_path = dest_path
                    final_seed = cur_seed

                    print(f"    Attempt {attempt}: Status={eval_res['status']}, FaceSim={eval_res['face_similarity']:.4f}, FeatScore={eval_res['feature_score']:.4f}", flush=True)

                    if not use_guard or eval_res["status"] == "Passed":
                        break
                    attempt += 1

                # Continuity & Action metrics
                img_emb = get_image_embedding(final_img_path)
                if turn.get("target_action") and turn.get("negative_actions"):
                    target_emb = get_text_embedding(turn["target_action"])
                    neg_embs = [get_text_embedding(na) for na in turn["negative_actions"]]
                    t_sim = cosine_similarity(img_emb, target_emb)
                    max_n_sim = max(cosine_similarity(img_emb, ne) for ne in neg_embs)
                    margin = float(t_sim - max_n_sim)
                    action_margins.append(margin)

                if prev_frame_emb is not None:
                    c_sim = float(cosine_similarity(img_emb, prev_frame_emb))
                    if is_trans:
                        trans_scene_sims.append(c_sim)
                    else:
                        same_scene_sims.append(c_sim)
                prev_frame_emb = img_emb

                # Quarantine & Last-Known-Good Logic
                is_passed = (final_eval["status"] == "Passed") or (not use_guard)
                if is_passed:
                    last_known_good_image = final_img_path
                    comfy_prev_name = f"{char_key}_pr24_current.png"
                    comfy_prev_path = os.path.join(INPUT_DIR, comfy_prev_name)
                    shutil.copyfile(final_img_path, comfy_prev_path)
                    last_known_good_image = comfy_prev_path
                else:
                    print(f"    ⚠️ [QUARANTINE] Turn {rev} failed all attempts! Quarantined from continuity lineage.", flush=True)

                frame_paths.append(final_img_path)
                frame_labels.append(f"T{rev} ({final_eval['status']}) - Sim:{final_eval['face_similarity']:.2f}")

                turn_results.append({
                    "scene_revision": int(rev),
                    "attempts_used": int(attempt if attempt <= max_attempts else max_attempts),
                    "final_seed": int(final_seed),
                    "evaluation": final_eval,
                    "is_current": bool(is_passed),
                    "image_file": os.path.basename(final_img_path)
                })

            # Create Contact Sheet
            contact_sheet_path = os.path.join(char_dir, f"{char_key}_{mode_id}_contact_sheet.png")
            create_contact_sheet(frame_paths, frame_labels, contact_sheet_path, avatar_local)

            mean_face = float(np.mean([t["evaluation"]["face_similarity"] for t in turn_results]))
            mean_feat = float(np.mean([t["evaluation"]["feature_score"] for t in turn_results]))
            pass_count = int(sum(1 for t in turn_results if t["evaluation"]["status"] == "Passed"))
            mean_margin = float(np.mean(action_margins)) if action_margins else 0.0
            mean_same = float(np.mean(same_scene_sims)) if same_scene_sims else 1.0
            mean_trans = float(np.mean(trans_scene_sims)) if trans_scene_sims else 0.0

            mode_summary["characters"][char_key] = {
                "name": char_name,
                "mean_face_similarity": round(mean_face, 4),
                "mean_feature_score": round(mean_feat, 4),
                "action_margin": round(mean_margin, 4),
                "same_scene_continuity": round(mean_same, 4),
                "transition_continuity": round(mean_trans, 4),
                "passed_frames": f"{pass_count}/{len(turns)}",
                "contact_sheet": os.path.basename(contact_sheet_path),
                "turns": turn_results
            }

        results.append(mode_summary)

    out_json = os.path.join(ARTIFACTS_DIR, f"stage_{1 if max_turns==4 else 2}_matrix.json")
    with open(out_json, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2)
        f.flush()
        os.fsync(f.fileno())

    print(f"\n==========================================================================", flush=True)
    print(f"✅ BENCHMARK COMPLETED! Summary saved to {out_json}", flush=True)
    print(f"==========================================================================", flush=True)

if __name__ == "__main__":
    stage = 1
    if "--stage" in sys.argv:
        idx = sys.argv.index("--stage")
        if idx + 1 < len(sys.argv):
            stage = int(sys.argv[idx + 1])
    run_benchmark(max_turns=4 if stage == 1 else 8)
