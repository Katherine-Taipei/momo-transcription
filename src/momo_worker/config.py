from pydantic import BaseModel

class Profile(BaseModel):
    compute_device: str = "cpu"
    whisper_model: str = "base"
    quantization: str = "int8"
    cpu_threads: int = 2
    diarization: bool = False
    alignment: bool = False
    chunk_length: int = 600

class JobRequest(BaseModel):
    job_id: str
    media_file_path: str
    chunk_output_dir: str
    profile: Profile
