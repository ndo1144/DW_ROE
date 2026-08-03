using Unity.Netcode.Components;
using UnityEngine;

namespace Project.Network
{
    /// <summary>
    /// 클라이언트(Owner) 권한으로 이동/회전 동기화를 처리하기 위한 커스텀 NetworkTransform입니다.
    /// 유니티 넷코드의 기본 NetworkTransform은 서버(Host) 권한이므로 클라이언트가 직접 이동할 수 없습니다.
    /// 이 컴포넌트를 사용하면 클라이언트가 자신의 위치를 직접 조작하고 서버로 동기화할 수 있습니다.
    /// </summary>
    [DisallowMultipleComponent]
    public class ClientNetworkTransform : NetworkTransform
    {
        protected override bool OnIsServerAuthoritative()
        {
            return false;
        }
    }
}
