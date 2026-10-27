# NOTĂ: contextul de build este rădăcina FederatedLearning/ (vezi docker-compose.fl.yml),
# ca să putem copia modulul comun fl_common/ partajat cu clientul.
FROM python:3.11-slim

WORKDIR /app

# Serverul face doar reconstituire + export ONNX (un singur forward), deci CPU e suficient.
# Build-args lăsate pentru simetrie cu clientul; nu e nevoie de GPU pe server.
ARG TORCH_INDEX_URL=https://download.pytorch.org/whl/cpu
ARG TORCH_PACKAGES="torch==2.3.1+cpu torchvision==0.18.1+cpu"
RUN pip install --no-cache-dir --extra-index-url ${TORCH_INDEX_URL} ${TORCH_PACKAGES}

COPY fl-server/requirements.txt ./requirements.txt
RUN pip install --no-cache-dir -r requirements.txt

COPY fl_common ./fl_common
COPY fl-server/server.py ./server.py
COPY fl-server/control_api.py ./control_api.py

EXPOSE 8080
EXPOSE 8081

CMD ["python", "control_api.py"]
