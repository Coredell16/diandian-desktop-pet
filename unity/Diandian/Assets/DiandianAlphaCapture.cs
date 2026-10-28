using UnityEngine;

// Sample the camera's image before Unity presents it to the Windows swap chain.
public sealed class DiandianAlphaCapture : MonoBehaviour
{
    public RenderTexture mask;
    void OnRenderImage(RenderTexture source,RenderTexture destination)
    {
        if(mask)Graphics.Blit(source,mask);
        Graphics.Blit(source,destination);
    }
}
