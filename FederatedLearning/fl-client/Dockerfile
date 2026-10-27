# NOTĂ: contextul de build este rădăcina FederatedLearning/ (vezi docker-compose.fl.yml),
# ca să putem copia modulul comun fl_common/ partajat cu serverul.
FROM python:3.11-slim

WORKDIR /app

# torch/torchvision: IMPLICIT build CPU. Pentru GPU NVIDIA, suprascrie build-args
# (vezi docker-compose.gpu.yml):
#   TORCH_INDEX_URL=https://download.pytorch.org/whl/cu121
#   TORCH_PACKAGES="torch==2.3.1+cu121 torchvision==0.18.1+cu121"
ARG TORCH_INDEX_URL=https://download.pytorch.org/whl/cpu
ARG TORCH_PACKAGES="torch==2.3.1+cpu torchvision==0.18.1+cpu"
RUN pip install --no-cache-dir --extra-index-url ${TORCH_INDEX_URL} ${TORCH_PACKAGES}

COPY fl-client/requirements.txt ./requirements.txt
RUN pip install --no-cache-dir -r requirements.txt

COPY fl_common ./fl_common
COPY fl-client/client.py ./client.py
COPY fl-client/model.py ./model.py
COPY fl-client/dataset.py ./dataset.py

CMD ["python", "client.py"]
