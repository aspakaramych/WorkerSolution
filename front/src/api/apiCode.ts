import axios from "axios";

export interface CodeExecutionRequest {
    code: string;
    language: "python" | "csharp" | "javascript" | "c++";
    correlationId: string;
}

interface CodeExecutionResponse {
    output: string;
    error: string;
    correlationId: string;
}

const apiWorker = axios.create({
    baseURL: "http://localhost:20001/CodeExecution",
})

export const api = async (data: CodeExecutionRequest): Promise<CodeExecutionResponse> => {
    try {
        const response = await apiWorker.post<CodeExecutionResponse>("/execute", data);
        return response.data;
    } catch (error) {
        console.log(error);
        throw error;
    }
}