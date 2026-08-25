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
EVAL_ARTIFACTS_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "eval_artifacts"))

CHARACTERS = [
    {
        "id": "character_01_lyra",
        "name": "Lyra",
        "title": "Silver Dragon Saintess",
        "avatar_filename": "Lyra_tight_face.png",
        "avatar_seed": 777111,
        "avatar_prompt": "masterpiece, best quality, solo, 1girl, long silver white hair, striking crimson red eyes, sharp black horns with glowing red accents on head, delicate porcelain skin, gentle expression, close up portrait, sharp focus",
        "attributes": {
            "gender": {"pos": "1girl, anime girl, female, woman", "neg": ["1man, anime man, male, boy"]},
            "hair": {"pos": "long silver white hair", "neg": ["short black hair", "bright yellow blonde hair", "pink hair"]},
            "eyes": {"pos": "striking crimson red eyes", "neg": ["blue eyes", "green eyes", "brown eyes"]},
            "feature": {"pos": "sharp black horns on head", "neg": ["no horns on head", "cat ears", "elf ears"]}
        },
        "turns": [
            {
                "turn": 1,
                "location": "Sanctuary (Standing Window)",
                "room": "Sanctuary",
                "is_transition": False,
                "action": "standing",
                "action_prompt": "an anime girl standing beside an arched window",
                "negative_action_prompts": ["an anime girl sitting on a chair", "an anime girl kneeling in prayer", "an anime girl lying on the floor"],
                "prompt": "masterpiece, best quality, solo, 1girl, long silver white hair, striking crimson red eyes, sharp black horns with glowing red accents on head, wearing white and gold silk priestess dress, standing beside grand arched window in sunlit sanctuary hall, soft golden daylight, medium shot, slight 3/4 turn, eye level",
                "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 100001
            },
            {
                "turn": 2,
                "location": "Sanctuary (Walking Altar)",
                "room": "Sanctuary",
                "is_transition": False,
                "action": "walking",
                "action_prompt": "an anime girl walking along an aisle holding a book",
                "negative_action_prompts": ["an anime girl sitting down", "an anime girl sleeping", "an anime girl lying down"],
                "prompt": "masterpiece, best quality, solo, 1girl, long silver white hair, striking crimson red eyes, sharp black horns with glowing red accents on head, wearing white and gold silk priestess dress, walking along marble aisle towards grand altar, holding ancient sacred scripture, streaming sunlight, medium shot, slight 3/4 turn",
                "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 100002
            },
            {
                "turn": 3,
                "location": "Sanctuary (Kneeling Prayer)",
                "room": "Sanctuary",
                "is_transition": False,
                "action": "kneeling",
                "action_prompt": "an anime girl kneeling in prayer before an altar hands clasped",
                "negative_action_prompts": ["an anime girl standing tall", "an anime girl running fast", "an anime girl dancing"],
                "prompt": "masterpiece, best quality, solo, 1girl, long silver white hair, striking crimson red eyes, sharp black horns with glowing red accents on head, wearing white and gold silk priestess dress, kneeling before golden altar in prayer, hands clasped, soft divine glowing aura, medium shot, slight 3/4 turn",
                "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 100003
            },
            {
                "turn": 4,
                "location": "Sanctuary (Smiling Turn)",
                "room": "Sanctuary",
                "is_transition": False,
                "action": "standing/smiling",
                "action_prompt": "an anime girl standing and smiling warmly looking at viewer",
                "negative_action_prompts": ["an anime girl crying sadly", "an anime girl sleeping", "an anime girl lying down"],
                "prompt": "masterpiece, best quality, solo, 1girl, long silver white hair, striking crimson red eyes, sharp black horns with glowing red accents on head, wearing white and gold silk priestess dress, standing gracefully near altar, looking towards viewer with a gentle affectionate smile, soft ambient light, medium shot",
                "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 100004
            },
            {
                "turn": 5,
                "location": "Library (Sitting Tea)",
                "room": "Library",
                "is_transition": True,
                "action": "sitting",
                "action_prompt": "an anime girl sitting at a wooden table drinking tea",
                "negative_action_prompts": ["an anime girl standing outside", "an anime girl running", "an anime girl lying on bed"],
                "prompt": "masterpiece, best quality, solo, 1girl, long silver white hair, striking crimson red eyes, sharp black horns with glowing red accents on head, wearing silk traveler cloak, sitting at wooden table in cozy library, holding warm ceramic teacup, warm ambient indoor light, medium shot, slight 3/4 turn",
                "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 100005
            },
            {
                "turn": 6,
                "location": "Library (Reading Grimoire)",
                "room": "Library",
                "is_transition": False,
                "action": "reading/leaning",
                "action_prompt": "an anime girl leaning over an open book reading a grimoire",
                "negative_action_prompts": ["an anime girl standing straight", "an anime girl dancing actively", "an anime girl sleeping"],
                "prompt": "masterpiece, best quality, solo, 1girl, long silver white hair, striking crimson red eyes, sharp black horns with glowing red accents on head, wearing silk traveler cloak, leaning over large open ancient grimoire on library desk, pointing at glowing magical runes, focused expression, medium shot",
                "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 100006
            },
            {
                "turn": 7,
                "location": "Balcony (Twilight Walk)",
                "room": "Balcony",
                "is_transition": True,
                "action": "walking",
                "action_prompt": "an anime girl walking on an outdoor stone balcony at twilight",
                "negative_action_prompts": ["an anime girl sitting inside a room", "an anime girl sleeping in bed"],
                "prompt": "masterpiece, best quality, solo, 1girl, long silver white hair, striking crimson red eyes, sharp black horns with glowing red accents on head, wearing silk traveler cloak, walking out onto palace stone balcony overlooking kingdom at dusk, gentle twilight breeze blowing hair, medium shot",
                "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 100007
            },
            {
                "turn": 8,
                "location": "Balcony (Night Stars)",
                "room": "Balcony",
                "is_transition": False,
                "action": "leaning/gazing",
                "action_prompt": "an anime girl leaning on a balcony railing looking up at stars in the night sky",
                "negative_action_prompts": ["an anime girl running fast", "an anime girl swimming in water", "an anime girl sitting on the floor"],
                "prompt": "masterpiece, best quality, solo, 1girl, long silver white hair, striking crimson red eyes, sharp black horns with glowing red accents on head, wearing silk traveler cloak, leaning on stone balcony railing at night, gazing up at starry sky and glowing moon, serene expression, medium shot",
                "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 100008
            }
        ]
    },
    {
        "id": "character_02_elysia",
        "name": "Elysia",
        "title": "High Elf Scholar",
        "avatar_filename": "Elysia_tight_face.png",
        "avatar_seed": 888222,
        "avatar_prompt": "masterpiece, best quality, solo, 1girl, pastel pink hair in soft waves, crystal bright blue eyes, elegant pointed elf ears, cute warm smile, close up portrait, soft natural light, sharp focus",
        "attributes": {
            "gender": {"pos": "1girl, anime girl, female, woman", "neg": ["1man, anime man, male, boy"]},
            "hair": {"pos": "pastel pink hair in waves", "neg": ["black hair", "blonde hair", "green hair"]},
            "eyes": {"pos": "crystal bright blue eyes", "neg": ["red eyes", "brown eyes", "dark black eyes"]},
            "feature": {"pos": "pointed elf ears", "neg": ["round human ears", "animal horns", "animal ears"]}
        },
        "turns": [
            {
                "turn": 1,
                "location": "Royal Academy (Lecture Podium)",
                "room": "Royal Academy",
                "is_transition": False,
                "action": "standing",
                "action_prompt": "an anime elf girl standing at a wooden lecture podium speaking",
                "negative_action_prompts": ["an anime elf girl sitting down", "an anime elf girl sleeping", "an anime elf girl running"],
                "prompt": "masterpiece, best quality, solo, 1girl, pastel pink hair in soft waves, crystal bright blue eyes, elegant pointed elf ears, wearing navy blue and gold academy scholar uniform, standing confidently at wooden lecture podium in grand sunlit academy hall, medium shot, slight 3/4 turn",
                "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 200001
            },
            {
                "turn": 2,
                "location": "Royal Academy (Walking Hallway)",
                "room": "Royal Academy",
                "is_transition": False,
                "action": "walking",
                "action_prompt": "an anime elf girl walking down a school hallway holding notebook",
                "negative_action_prompts": ["an anime elf girl lying down", "an anime elf girl sitting on bench", "an anime elf girl fighting"],
                "prompt": "masterpiece, best quality, solo, 1girl, pastel pink hair, crystal blue eyes, pointed elf ears, wearing academy uniform, walking gracefully down sun-drenched academy hallway with stained glass reflections, holding leather notebook, cheerful expression, medium shot",
                "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 200002
            },
            {
                "turn": 3,
                "location": "Rose Garden (Sitting Bench)",
                "room": "Rose Garden",
                "is_transition": True,
                "action": "sitting",
                "action_prompt": "an anime elf girl sitting on a garden bench smelling a flower",
                "negative_action_prompts": ["an anime elf girl standing upright", "an anime elf girl running", "an anime elf girl swimming"],
                "prompt": "masterpiece, best quality, solo, 1girl, pastel pink hair, crystal blue eyes, pointed elf ears, wearing floral white summer dress, sitting on ornate white iron bench in lush blooming rose garden, gently holding and smelling pink rose, soft afternoon sun, medium shot",
                "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 200003
            },
            {
                "turn": 4,
                "location": "Rose Garden (Picking Petals)",
                "room": "Rose Garden",
                "is_transition": False,
                "action": "kneeling",
                "action_prompt": "an anime elf girl kneeling by flowerbeds tending plants",
                "negative_action_prompts": ["an anime elf girl sitting inside", "an anime elf girl sleeping in bed", "an anime elf girl running"],
                "prompt": "masterpiece, best quality, solo, 1girl, pastel pink hair, crystal blue eyes, pointed elf ears, wearing floral white summer dress, kneeling gently beside garden flowerbed, touching blooming blossoms with delicate fingers, sunlight filtering through leaves, medium shot",
                "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 200004
            },
            {
                "turn": 5,
                "location": "Rose Garden (Laughing Turn)",
                "room": "Rose Garden",
                "is_transition": False,
                "action": "standing/smiling",
                "action_prompt": "an anime elf girl standing in garden laughing happily looking at viewer",
                "negative_action_prompts": ["an anime elf girl crying", "an anime elf girl sleeping", "an anime elf girl sitting"],
                "prompt": "masterpiece, best quality, solo, 1girl, pastel pink hair, crystal blue eyes, pointed elf ears, wearing floral white summer dress, standing playfully on cobblestone garden path, turning back towards viewer with a radiant happy laugh, blooming roses surrounding, medium shot",
                "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 200005
            },
            {
                "turn": 6,
                "location": "Clock Tower (Observatory Desk)",
                "room": "Clock Tower",
                "is_transition": True,
                "action": "sitting",
                "action_prompt": "an anime elf girl sitting at desk examining astronomical charts",
                "negative_action_prompts": ["an anime elf girl running fast", "an anime elf girl standing tall", "an anime elf girl swimming"],
                "prompt": "masterpiece, best quality, solo, 1girl, pastel pink hair, crystal blue eyes, pointed elf ears, wearing midnight blue velvet evening gown, sitting at antique brass desk in grand clock tower observatory, examining glowing celestial star charts, soft lantern glow, medium shot",
                "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 200006
            },
            {
                "turn": 7,
                "location": "Clock Tower (Telescope Window)",
                "room": "Clock Tower",
                "is_transition": False,
                "action": "standing/leaning",
                "action_prompt": "an anime elf girl standing peering through brass telescope",
                "negative_action_prompts": ["an anime elf girl lying on floor", "an anime elf girl dancing actively"],
                "prompt": "masterpiece, best quality, solo, 1girl, pastel pink hair, crystal blue eyes, pointed elf ears, wearing midnight blue velvet evening gown, standing beside large brass telescope near open clock tower window, looking up at constellations, starry night sky background, medium shot",
                "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 200007
            },
            {
                "turn": 8,
                "location": "Clock Tower (Night Reflection)",
                "room": "Clock Tower",
                "is_transition": False,
                "action": "standing/smiling",
                "action_prompt": "an anime elf girl standing quietly smiling under moonlight",
                "negative_action_prompts": ["an anime elf girl crying", "an anime elf girl running outside", "an anime elf girl sleeping"],
                "prompt": "masterpiece, best quality, solo, 1girl, pastel pink hair, crystal blue eyes, pointed elf ears, wearing midnight blue velvet evening gown, standing gracefully in moonlit clock tower room, soft gentle gaze, silver moonlight highlighting hair and face, medium shot",
                "negative": "2girls, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 200008
            }
        ]
    },
    {
        "id": "character_03_valerius",
        "name": "Valerius",
        "title": "Shadow Knight Commander",
        "avatar_filename": "Valerius_tight_face.png",
        "avatar_seed": 999333,
        "avatar_prompt": "masterpiece, best quality, solo, 1man, short textured jet black hair, sharp piercing golden amber eyes, chiseled handsome jawline, calm stern expression, dark silver armor collar, close up portrait, dramatic lighting",
        "attributes": {
            "gender": {"pos": "1man, handsome anime man, male knight, adult man", "neg": ["1girl, anime girl, female, woman, breasts"]},
            "hair": {"pos": "short textured black hair", "neg": ["long blonde hair", "pink hair", "white hair"]},
            "eyes": {"pos": "piercing golden amber eyes", "neg": ["blue eyes", "bright green eyes", "pink eyes"]},
            "feature": {"pos": "dark commander knight armor and cloak", "neg": ["casual t-shirt", "white silk wedding dress", "swimsuit"]}
        },
        "turns": [
            {
                "turn": 1,
                "location": "Armory (Inspecting Blade)",
                "room": "Armory",
                "is_transition": False,
                "action": "standing",
                "action_prompt": "an anime knight man standing holding a sheathed sword",
                "negative_action_prompts": ["an anime knight sitting down", "an anime knight sleeping", "an anime knight lying on ground"],
                "prompt": "masterpiece, best quality, solo, 1man, short textured black hair, piercing golden amber eyes, handsome sharp face, wearing dark steel knight armor with silver trims, standing resolutely in fortress armory holding sheathed longsword, torchlight reflections on metal, medium shot, slight 3/4 turn",
                "negative": "2men, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 300001
            },
            {
                "turn": 2,
                "location": "Armory (Polishing Armor)",
                "room": "Armory",
                "is_transition": False,
                "action": "sitting",
                "action_prompt": "an anime knight man sitting at a workbench maintaining equipment",
                "negative_action_prompts": ["an anime knight running outdoors", "an anime knight dancing", "an anime knight swimming"],
                "prompt": "masterpiece, best quality, solo, 1man, short textured black hair, piercing amber eyes, dark steel knight armor, sitting at heavy wooden armory workbench, cleaning gauntlet with cloth, focused determined expression, warm forge glow, medium shot",
                "negative": "2men, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 300002
            },
            {
                "turn": 3,
                "location": "War Room (Studying Map)",
                "room": "War Room",
                "is_transition": True,
                "action": "leaning",
                "action_prompt": "an anime knight man leaning over a war map planning strategy",
                "negative_action_prompts": ["an anime knight sleeping in bed", "an anime knight dancing", "an anime knight sitting on floor"],
                "prompt": "masterpiece, best quality, solo, 1man, short textured black hair, piercing amber eyes, wearing dark military commander tunic with silver cloak, leaning forward over large parchment battle map on stone table in war room, strategic intense gaze, flickering candlelight, medium shot",
                "negative": "2men, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 300003
            },
            {
                "turn": 4,
                "location": "War Room (Standing Briefing)",
                "room": "War Room",
                "is_transition": False,
                "action": "standing",
                "action_prompt": "an anime knight man standing tall delivering orders",
                "negative_action_prompts": ["an anime knight crying", "an anime knight sleeping", "an anime knight lying down"],
                "prompt": "masterpiece, best quality, solo, 1man, short textured black hair, piercing amber eyes, dark commander tunic and cloak, standing tall with arms crossed beside war table, authoritative calm posture, warm ambient chamber light, medium shot",
                "negative": "2men, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 300004
            },
            {
                "turn": 5,
                "location": "War Room (Wine Cup Rest)",
                "room": "War Room",
                "is_transition": False,
                "action": "sitting",
                "action_prompt": "an anime knight man sitting in a high-backed chair holding wine goblet",
                "negative_action_prompts": ["an anime knight running fast", "an anime knight kneeling in mud"],
                "prompt": "masterpiece, best quality, solo, 1man, short textured black hair, piercing amber eyes, dark commander tunic, sitting relaxed in carved high-backed oak chair, holding silver goblet, subtle relaxed smirk, fireplace glow in background, medium shot",
                "negative": "2men, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 300005
            },
            {
                "turn": 6,
                "location": "Battlements (Gazing Kingdom)",
                "room": "Battlements",
                "is_transition": True,
                "action": "standing/leaning",
                "action_prompt": "an anime knight man standing leaning on stone fortress battlement looking at kingdom",
                "negative_action_prompts": ["an anime knight sitting inside", "an anime knight sleeping in bed"],
                "prompt": "masterpiece, best quality, solo, 1man, short textured black hair, piercing amber eyes, wearing full commander armor and dark flowing cloak, standing on high fortress battlements, leaning against stone parapet looking out at sprawling kingdom at sunset, wind whipping cloak, medium shot",
                "negative": "2men, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 300006
            },
            {
                "turn": 7,
                "location": "Battlements (Night Patrol Walk)",
                "room": "Battlements",
                "is_transition": False,
                "action": "walking",
                "action_prompt": "an anime knight man walking along castle wall at night with lantern",
                "negative_action_prompts": ["an anime knight sitting in chair", "an anime knight dancing"],
                "prompt": "masterpiece, best quality, solo, 1man, short textured black hair, piercing amber eyes, commander armor and cloak, walking firmly along moonlit castle wall, carrying iron lantern casting dynamic shadows, starry night sky, medium shot",
                "negative": "2men, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 300007
            },
            {
                "turn": 8,
                "location": "Battlements (Salute to Dawn)",
                "room": "Battlements",
                "is_transition": False,
                "action": "standing",
                "action_prompt": "an anime knight man standing at attention saluting dawn horizon",
                "negative_action_prompts": ["an anime knight sleeping", "an anime knight lying on ground"],
                "prompt": "masterpiece, best quality, solo, 1man, short textured black hair, piercing amber eyes, commander armor, standing tall on eastern ramparts, placing fist over chest in salute towards rising morning sun, first dawn rays illuminating armor and face, majestic lighting, medium shot",
                "negative": "2men, multiple people, bad anatomy, bad hands, missing fingers, cropped, blurry, low quality",
                "seed": 300008
            }
        ]
    }
]

def load_v2_workflow_template():
    template_path = os.path.join(os.path.dirname(__file__), "production_workflow_v2_template.json")
    if not os.path.exists(template_path):
        raise FileNotFoundError(f"V2 template missing at {template_path}. Run 'dotnet test' to generate it.")
    with open(template_path, "r", encoding="utf-8") as f:
        return json.load(f)

def build_workflow(template, avatar_img_name, prev_scene_img_name, prompt, negative, seed):
    wf = json.loads(json.dumps(template))
    wf["1"]["inputs"]["image"] = avatar_img_name
    wf["6"]["inputs"]["text"] = prompt
    wf["7"]["inputs"]["text"] = negative
    wf["3"]["inputs"]["seed"] = int(seed)

    # Slot 1: Identity Conditioning (0.60 / 0.85)
    wf["10"]["inputs"]["weight"] = 0.60
    wf["10"]["inputs"]["end_at"] = 0.85

    # Slot 2: Previous Scene Continuity Prior (0.20 / 0.40)
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

def build_avatar_txt2img_workflow(prompt, negative, seed):
    return {
        "4": {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": "meinamix_meinaV11.safetensors"}},
        "5": {"class_type": "EmptyLatentImage", "inputs": {"width": 512, "height": 768, "batch_size": 1}},
        "6": {"class_type": "CLIPTextEncode", "inputs": {"text": prompt, "clip": ["4", 1]}},
        "7": {"class_type": "CLIPTextEncode", "inputs": {"text": negative, "clip": ["4", 1]}},
        "3": {"class_type": "KSampler", "inputs": {"model": ["4", 0], "positive": ["6", 0], "negative": ["7", 0], "latent_image": ["5", 0], "seed": seed, "steps": 28, "cfg": 7.0, "sampler_name": "euler_ancestral", "scheduler": "karras", "denoise": 1.0}},
        "9": {"class_type": "VAEDecode", "inputs": {"samples": ["3", 0], "vae": ["4", 2]}},
        "11": {"class_type": "SaveImage", "inputs": {"filename_prefix": "AvatarCanonical", "images": ["9", 0]}}
    }

def queue_prompt(prompt_workflow):
    data = json.dumps({"prompt": prompt_workflow}).encode("utf-8")
    req = urllib.request.Request(f"{COMFY_URL}/prompt", data=data, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req) as resp:
        return json.loads(resp.read().decode("utf-8"))

def wait_for_prompt_completion(prompt_id, timeout_sec=300):
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
        print("Initializing CLIP-ViT-H-14 Vision & Text Models on CPU...")
        self.device = "cpu"
        self.processor = CLIPImageProcessor.from_pretrained("laion/CLIP-ViT-H-14-laion2B-s32B-b79K")
        self.vision_model = CLIPVisionModelWithProjection.from_pretrained(
            "laion/CLIP-ViT-H-14-laion2B-s32B-b79K"
        ).to(self.device)
        self.vision_model.eval()

        self.tokenizer = CLIPTokenizer.from_pretrained("laion/CLIP-ViT-H-14-laion2B-s32B-b79K")
        self.text_model = CLIPTextModelWithProjection.from_pretrained(
            "laion/CLIP-ViT-H-14-laion2B-s32B-b79K"
        ).to(self.device)
        self.text_model.eval()
        print("CLIP-ViT-H-14 Models loaded successfully on CPU.")

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

    def evaluate_action_compliance(self, img_path: str, target_prompt: str, negative_prompts: list):
        img_emb = self.get_full_image_embedding(img_path)
        pos_emb = self.get_text_embedding(target_prompt)
        pos_sim = self.compute_similarity(img_emb, pos_emb)

        neg_sims = [self.compute_similarity(img_emb, self.get_text_embedding(neg)) for neg in negative_prompts]
        max_neg_sim = max(neg_sims) if neg_sims else 0.0

        margin = pos_sim - max_neg_sim
        is_compliant = margin > 0.0

        return {
            "pos_sim": pos_sim,
            "max_neg_sim": max_neg_sim,
            "margin": margin,
            "is_compliant": is_compliant
        }

    def evaluate_attribute(self, img_path: str, attr_def: dict):
        img_emb = self.get_full_image_embedding(img_path)
        pos_emb = self.get_text_embedding(attr_def["pos"])
        pos_sim = self.compute_similarity(img_emb, pos_emb)

        neg_sims = [self.compute_similarity(img_emb, self.get_text_embedding(neg)) for neg in attr_def["neg"]]
        max_neg_sim = max(neg_sims) if neg_sims else 0.0

        margin = pos_sim - max_neg_sim
        is_retained = margin > 0.0
        return is_retained, margin

def create_contact_sheet(char_dir: str, char_name: str, images_dict: dict, output_path: str):
    tile_w, tile_h = 320, 390
    cols, rows = 3, 3
    sheet_w, sheet_h = tile_w * cols, tile_h * rows
    sheet = Image.new("RGB", (sheet_w, sheet_h), color=(20, 20, 25))
    draw = ImageDraw.Draw(sheet)

    grid_items = [
        ("CANONICAL AVATAR", images_dict.get("canonical")),
        ("TURN 1 (T1)", images_dict.get(1)),
        ("TURN 2 (T2)", images_dict.get(2)),
        ("TURN 3 (T3)", images_dict.get(3)),
        ("TURN 4 (T4)", images_dict.get(4)),
        ("TURN 5 (T5)", images_dict.get(5)),
        ("TURN 6 (T6)", images_dict.get(6)),
        ("TURN 7 (T7)", images_dict.get(7)),
        ("TURN 8 (T8)", images_dict.get(8)),
    ]

    for idx, (label, img_path) in enumerate(grid_items):
        c = idx % cols
        r = idx // cols
        x = c * tile_w
        y = r * tile_h

        if img_path and os.path.exists(img_path):
            tile_img = Image.open(img_path).convert("RGB")
            tile_img = tile_img.resize((tile_w - 10, tile_h - 40), Image.Resampling.LANCZOS)
            sheet.paste(tile_img, (x + 5, y + 30))

        draw.rectangle([(x + 5, y + 5), (x + tile_w - 5, y + 28)], fill=(35, 35, 45))
        draw.text((x + 12, y + 8), label, fill=(240, 240, 250))

    sheet.save(output_path, quality=95)
    print(f"[{char_name}] Contact sheet generated: {output_path}")

def ensure_canonical_avatar(char, evaluator):
    avatar_path = os.path.join(COMFY_INPUT_DIR, char["avatar_filename"])
    if os.path.exists(avatar_path):
        print(f"[{char['name']}] Canonical avatar found: {avatar_path}")
        return avatar_path

    print(f"[{char['name']}] Generating canonical avatar via ComfyUI txt2img...")
    wf = build_avatar_txt2img_workflow(char["avatar_prompt"], "bad anatomy, blurry, low quality", char["avatar_seed"])
    q = queue_prompt(wf)
    gen_path = wait_for_prompt_completion(q["prompt_id"])

    raw_img = Image.open(gen_path).convert("RGB")
    face_crop = evaluator.locate_and_crop_face_region(raw_img)
    face_crop.save(avatar_path)
    print(f"[{char['name']}] Generated & cropped tight face avatar saved: {avatar_path}")
    return avatar_path

def evaluate_character(char, template, evaluator):
    print(f"\n==========================================================================================")
    print(f"EVALUATING PRODUCTION CHARACTER: {char['name'].upper()} ({char['title']})")
    print(f"==========================================================================================")

    char_dir = os.path.join(EVAL_ARTIFACTS_DIR, char["id"])
    os.makedirs(char_dir, exist_ok=True)

    avatar_path = ensure_canonical_avatar(char, evaluator)
    canonical_artifact_path = os.path.join(char_dir, "canonical_avatar.png")
    shutil.copyfile(avatar_path, canonical_artifact_path)
    avatar_emb = evaluator.get_face_embedding(avatar_path)

    results = []
    prev_scene_filename = None
    prev_full_emb = None
    images_dict = {"canonical": canonical_artifact_path}

    for step in char["turns"]:
        turn = step["turn"]
        location = step["location"]
        action = step["action"]
        prompt = step["prompt"]
        negative = step["negative"]
        seed = step["seed"]
        target_action_prompt = step["action_prompt"]
        neg_action_prompts = step["negative_action_prompts"]
        is_transition = step["is_transition"]

        wf = build_workflow(template, char["avatar_filename"], prev_scene_filename, prompt, negative, seed)
        q = queue_prompt(wf)
        gen_path = wait_for_prompt_completion(q["prompt_id"])

        artifact_filename = f"turn_{turn:02d}_{action.replace('/', '_')}.png"
        artifact_path = os.path.join(char_dir, artifact_filename)
        shutil.copyfile(gen_path, artifact_path)
        images_dict[turn] = artifact_path

        next_input_filename = f"{char['id']}_turn_{turn}_input.png"
        next_input_path = os.path.join(COMFY_INPUT_DIR, next_input_filename)
        shutil.copyfile(gen_path, next_input_path)

        # 1. CLIPIdentitySim (Face crop visual identity similarity)
        face_emb = evaluator.get_face_embedding(gen_path)
        clip_identity_sim = evaluator.compute_similarity(avatar_emb, face_emb)

        # 2. Scene Continuity (Same-scene vs Transition)
        full_emb = evaluator.get_full_image_embedding(gen_path)
        scene_sim = evaluator.compute_similarity(prev_full_emb, full_emb) if prev_full_emb is not None else 1.0

        # 3. Action Discrimination
        action_metrics = evaluator.evaluate_action_compliance(gen_path, target_action_prompt, neg_action_prompts)

        # 4. Attribute Invariants Retention
        gender_ok, _ = evaluator.evaluate_attribute(gen_path, char["attributes"]["gender"])
        hair_ok, _ = evaluator.evaluate_attribute(gen_path, char["attributes"]["hair"])
        eyes_ok, _ = evaluator.evaluate_attribute(gen_path, char["attributes"]["eyes"])
        feat_ok, _ = evaluator.evaluate_attribute(gen_path, char["attributes"]["feature"])

        trans_tag = "[TRANSITION]" if is_transition else "[SAME-SCENE]"
        comp_tag = "PASS" if action_metrics["is_compliant"] else "WARN"
        print(f"[{char['name']} | Turn {turn}/8] {location:<32} {trans_tag:<12} | Identity: {clip_identity_sim:.4f} | Scene: {scene_sim:.4f} | Margin: {action_metrics['margin']:+.4f} [{comp_tag}] | Gender:{'✓' if gender_ok else '✗'} Hair:{'✓' if hair_ok else '✗'} Eye:{'✓' if eyes_ok else '✗'} Feat:{'✓' if feat_ok else '✗'}")

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

    # Generate Contact Sheet
    contact_sheet_path = os.path.join(char_dir, f"{char['id']}_contact_sheet.png")
    create_contact_sheet(char_dir, char["name"], images_dict, contact_sheet_path)

    # Compute Statistics
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
        "character": char["name"],
        "title": char["title"],
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
    print("PROJECT00: MULTI-CHARACTER PRODUCTION VISUAL QUALITY & CONTINUITY EVALUATION (PR #22)")
    print("Measuring CLIPIdentitySim, Split SceneSim, Action Margins, Attribute Retention across 24 Real Frames")
    print("=" * 110)

    template = load_v2_workflow_template()
    evaluator = ComprehensiveEvaluator()

    all_summaries = []
    for char in CHARACTERS:
        summary = evaluate_character(char, template, evaluator)
        all_summaries.append(summary)

    summary_json_path = os.path.join(EVAL_ARTIFACTS_DIR, "production_evaluation_summary.json")
    with open(summary_json_path, "w", encoding="utf-8") as f:
        json.dump(all_summaries, f, indent=2)

    print("\n" + "=" * 120)
    print("AGGREGATED MULTI-CHARACTER PRODUCTION EVALUATION SUMMARY")
    print("=" * 120)
    print(f"{'Character Persona':<25} | {'Mean Identity':<14} | {'Degradation Slope':<18} | {'Same-Scene Sim':<15} | {'Trans Scene Sim':<16} | {'Action Margin':<14} | {'Hair/Eye/Feat'}")
    print("-" * 120)
    for s in all_summaries:
        print(f"{s['character'] + ' (' + s['title'] + ')':<25} | {s['mean_identity_sim']:<14.4f} | {s['identity_slope']:<+18.5f} | {s['mean_same_scene_continuity']:<15.4f} | {s['mean_transition_continuity']:<16.4f} | {s['mean_action_margin']:<+14.4f} | {s['hair_retention']}/{s['eye_retention']}/{s['feature_retention']}")
    print("=" * 120)
    print(f"Artifacts, full frame PNGs, and contact sheets saved to: {EVAL_ARTIFACTS_DIR}")

if __name__ == '__main__':
    try:
        main()
    except Exception as e:
        print("CRITICAL ERROR IN MAIN:", e)
        traceback.print_exc()
        sys.exit(1)

