"""Background removal for TRELLIS.2 built from the vendored BiRefNet code, without trust_remote_code.

TRELLIS.2's rembg wrapper loads its model with trust_remote_code=True, which downloads and runs
model code from Hugging Face, and its pipeline.json names briaai/RMBG-2.0 (gated, and its code
differs from the public BiRefNet code). LocalBiRefNet keeps the wrapper's preprocessing and
inference unchanged and builds the model from third_party/BiRefNet with the matching public
ZhengPeng7/BiRefNet weights. Only TrellisGenerator.load() imports this module, after the vendored
TRELLIS.2 is on sys.path.
"""

import importlib.util
from pathlib import Path
import sys

from torchvision import transforms
from trellis2.pipelines import rembg

BIREFNET_ROOT = Path(__file__).resolve().parent.parent / "third_party" / "BiRefNet"
BIREFNET_WEIGHTS = "ZhengPeng7/BiRefNet"
_PACKAGE = "vrcrafter_vendored_birefnet"


def _vendored_birefnet():
    """Import third_party/BiRefNet as a package under a private name (its modules use relative imports)."""
    if _PACKAGE not in sys.modules:
        spec = importlib.util.spec_from_file_location(
            _PACKAGE, BIREFNET_ROOT / "__init__.py", submodule_search_locations=[str(BIREFNET_ROOT)]
        )
        module = importlib.util.module_from_spec(spec)
        sys.modules[_PACKAGE] = module
        spec.loader.exec_module(module)
    return sys.modules[_PACKAGE]


class LocalBiRefNet(rembg.BiRefNet):
    def __init__(self, model_name: str = BIREFNET_WEIGHTS):
        del model_name  # pipeline.json's briaai/RMBG-2.0 is replaced by the weights matching the vendored code
        self.model = _vendored_birefnet().BiRefNet.from_pretrained(BIREFNET_WEIGHTS)
        self.model.eval()
        # Same transform as the upstream wrapper's __init__; to/cuda/cpu/__call__ are inherited unchanged.
        self.transform_image = transforms.Compose(
            [
                transforms.Resize((1024, 1024)),
                transforms.ToTensor(),
                transforms.Normalize([0.485, 0.456, 0.406], [0.229, 0.224, 0.225]),
            ]
        )
